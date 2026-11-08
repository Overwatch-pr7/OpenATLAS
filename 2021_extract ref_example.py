import os
import time
import fastf1
import numpy as np
import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq
from scipy.interpolate import interp1d

# 1. Setup Directories & Enable Local Cache (NO LOGIN/AUTH REQUIRED)
CACHE_DIR = "./f1_cache"
OUTPUT_DIR = "./parquet_2021"
os.makedirs(CACHE_DIR, exist_ok=True)
os.makedirs(OUTPUT_DIR, exist_ok=True)

fastf1.Cache.enable_cache(CACHE_DIR)

# 2. Strict Non-Nullable PyArrow Schema (Prevents C# Parquet.Net MaxDefinitionLevel Crash)
CSHARP_SAFE_SCHEMA = pa.schema([
    pa.field("Distance", pa.float32(), nullable=False),
    pa.field("TimeSec", pa.float32(), nullable=False),
    pa.field("Speed", pa.float32(), nullable=False),
    pa.field("Throttle", pa.float32(), nullable=False),
    pa.field("Brake", pa.float32(), nullable=False),
    pa.field("RPM", pa.float32(), nullable=False),
    pa.field("Gear", pa.int32(), nullable=False),
])

def process_and_export_session(year: int, round_num: int, event_name: str, session_type: str):
    safe_event = event_name.lower().replace(" ", "_").replace("-", "_")
    
    # Load session telemetry (weather & messages disabled for fast downloading)
    session = fastf1.get_session(year, round_num, session_type)
    session.load(telemetry=True, weather=False, messages=False)
    
    if session.laps.empty:
        print(f"  [SKIP] No laps recorded for Round {round_num} ({session_type})")
        return

    # Pick the overall fastest valid lap of the session
    fastest_lap = session.laps.pick_fastest()
    if fastest_lap is None or pd.isna(fastest_lap['LapTime']):
        print(f"  [SKIP] No valid fastest lap found for Round {round_num} ({session_type})")
        return

    driver = fastest_lap['Driver']
    output_filename = f"{year}_R{round_num:02d}_{safe_event}_{session_type}_{driver}.parquet"
    output_path = os.path.join(OUTPUT_DIR, output_filename)

    # Skip if already exported in a previous run
    if os.path.exists(output_path):
        print(f"  [EXISTS] {output_filename} already generated. Skipping.")
        return

    # Extract car telemetry and compute cumulative distance (meters)
    tel = fastest_lap.get_car_data().add_distance()
    tel['TimeSec'] = tel['Time'].dt.total_seconds()

    # Clean duplicate distance ticks and NaNs before interpolation
    tel = (
        tel.drop_duplicates(subset=['Distance'])
        .dropna(subset=['Distance', 'Speed', 'TimeSec'])
        .sort_values('Distance')
    )

    max_dist = int(np.floor(tel['Distance'].max()))
    if max_dist < 1000:
        print(f"  [WARN] Track distance unusually short ({max_dist}m), skipping.")
        return

    # Resample onto a uniform 1-meter distance grid: 0m, 1m, 2m ... max_dist
    uniform_dist = np.arange(0, max_dist, 1.0, dtype=np.float32)

    interp_time = interp1d(tel['Distance'], tel['TimeSec'], kind='linear', fill_value="extrapolate")(uniform_dist)
    interp_speed = interp1d(tel['Distance'], tel['Speed'], kind='linear', fill_value="extrapolate")(uniform_dist)
    interp_throttle = interp1d(tel['Distance'], tel['Throttle'], kind='linear', fill_value="extrapolate")(uniform_dist)
    interp_brake = interp1d(tel['Distance'].values, tel['Brake'].astype(float).values, kind='nearest', fill_value="extrapolate")(uniform_dist)
    interp_rpm = interp1d(tel['Distance'], tel['RPM'], kind='linear', fill_value="extrapolate")(uniform_dist)
    interp_gear = interp1d(tel['Distance'], tel['nGear'], kind='previous', fill_value="extrapolate")(uniform_dist)

    df_resampled = pd.DataFrame({
        "Distance": uniform_dist.astype(np.float32),
        "TimeSec": interp_time.astype(np.float32),
        "Speed": interp_speed.astype(np.float32),
        "Throttle": np.clip(interp_throttle, 0.0, 100.0).astype(np.float32),
        "Brake": np.clip(interp_brake, 0.0, 100.0).astype(np.float32),
        "RPM": interp_rpm.astype(np.float32),
        "Gear": interp_gear.astype(np.int32),
    })

    # Write C#-compatible Parquet file with nullable=False enforced
    table = pa.Table.from_pandas(df_resampled, schema=CSHARP_SAFE_SCHEMA, preserve_index=False)
    pq.write_table(table, output_path, compression='snappy')
    print(f"  [OK] Saved {output_filename} ({max_dist}m | Fastest: {driver})")

def download_full_2021_season(sessions_to_pull=('Q', 'R')):
    year = 2021
    schedule = fastf1.get_event_schedule(year, include_testing=False)
    
    print(f"Found {len(schedule)} official Grand Prix rounds for {year}.\n")

    for _, event in schedule.iterrows():
        round_num = int(event['RoundNumber'])
        event_name = event['EventName']
        print(f"=== Round {round_num:02d}: {event_name} ===")

        for ses_type in sessions_to_pull:
            try:
                process_and_export_session(year, round_num, event_name, ses_type)
                # Polite delay so the Ergast/Jolpica API never rate-limits us
                time.sleep(3)
            except Exception as e:
                print(f"  [ERROR] Round {round_num:02d} {event_name} ({ses_type}) failed: {e}")
                time.sleep(5)

if __name__ == "__main__":
    # Pulls both Qualifying ('Q') and Race ('R') fastest reference laps for all 22 rounds of 2021
    download_full_2021_season(sessions_to_pull=('Q', 'R'))