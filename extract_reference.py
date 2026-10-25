"""
OpenATLAS - Phase 1: Reference Data Pipeline
Task 1.1: FastF1 Session Downloader

This script uses the `fastf1` library to download real-world telemetry from
an official F1 session. It serves as the baseline data for the OpenATLAS telemetry tool.

--- HELP BLOCK: Selecting Races ---
You can select different sessions by changing the parameters in fastf1.get_session(year, Grand_Prix, Session).

1. Year: The championship year (e.g., 2021, 2022, 2023, 2024).
2. Grand Prix: The name of the race or its location. You can use common names:
   - "Monza" or "Italian Grand Prix"
   - "Silverstone" or "British Grand Prix"
   - "Spa" or "Belgian Grand Prix"
   - "Monaco" or "Monaco Grand Prix"
3. Session: The specific session of the weekend. Options include:
   - 'FP1', 'FP2', 'FP3' (Free Practice sessions)
   - 'Q' (Qualifying)
   - 'S' (Sprint)
   - 'SQ' (Sprint Shootout)
   - 'R' (Race)

Example Usage:
session = fastf1.get_session(2023, 'Monza', 'Q') # 2023 Italian GP Qualifying
session = fastf1.get_session(2021, 'Abu Dhabi', 'R') # 2021 Abu Dhabi GP Race
-----------------------------------

--- Reference Library (used by the Workbench dropdowns) ---
Laps are saved as: references/<Year>/<Track>/<DRIVER>.parquet
<Track> is the event location (e.g. 'Monza', 'Silverstone', 'Spa-Francorchamps').

Sources:
  openf1 (default) - free OpenF1 API, 2023 season onward, no account needed
  fastf1           - FastF1, needs an F1TV sign-in (python -m fastf1 auth f1tv --authenticate)
                     unless the session is already in ./cache

Command line:
python extract_reference.py --year 2023 --track Monza --driver HAM   # one driver
python extract_reference.py --year 2023 --track Monza                # every driver in the session
python extract_reference.py --year 2024 --track Silverstone --session R
python extract_reference.py --year 2023 --track Monza --source fastf1
-----------------------------------------------------------
"""

import os
import argparse
import fastf1
import numpy as np
import pandas as pd
from scipy.interpolate import interp1d
import openf1_source

def LoadSession(Year: int, GrandPrix: str, SessionType: str):
    """
    Downloads and loads a session. One load contains every driver's laps,
    so exporting the whole grid costs a single download.

    Args:
        Year (int): The year of the race (e.g., 2023).
        GrandPrix (str): The name or location of the Grand Prix (e.g., 'Monza').
        SessionType (str): The session identifier (e.g., 'Q' for Qualifying).
    """
    print(f"Loading session: {Year} {GrandPrix} - {SessionType}")

    # Enable cache to speed up subsequent requests and prevent redundant API calls
    # We create the cache directory automatically if it doesn't exist
    os.makedirs('cache', exist_ok=True)
    fastf1.Cache.enable_cache('cache')

    session = fastf1.get_session(Year, GrandPrix, SessionType)

    # Only laps and car telemetry are needed for a reference lap
    session.load(laps=True, telemetry=True, weather=False, messages=False)

    # livetiming.formula1.com needs an F1TV sign-in for sessions not already in the cache;
    # without it the load "succeeds" but contains no laps
    try:
        session.laps
    except fastf1.exceptions.DataNotLoadedError:
        raise SystemExit(
            "\nNo lap data was downloaded for this session.\n"
            "F1 now requires an F1TV account for timing data. Sign in once with:\n"
            "    python -m fastf1 auth f1tv\n"
            "then run this script again.")
    return session

def DownloadSessionData(session, Driver: str):
    """
    Extracts the fastest lap telemetry for a specific driver from a loaded session.

    Args:
        session: A session returned by LoadSession.
        Driver (str): The three-letter abbreviation of the driver (e.g., 'HAM' for Lewis Hamilton).
    """
    try:
        # Pick the fastest lap for the specified driver
        driverLap = session.laps.pick_drivers(Driver).pick_fastest()
        if driverLap is None or driverLap.empty:
            print(f"  {Driver}: no timed lap, skipping")
            return None

        # Get the raw telemetry data (Speed, Throttle, Brake, RPM, Gear, etc.)
        telemetry = driverLap.get_telemetry()

        print(f"  {Driver}: telemetry extracted ({len(telemetry)} rows)")
        return telemetry

    except Exception as e:
        print(f"  {Driver}: an error occurred while extracting the lap: {e}")
        return None

def ResampleTelemetry(telemetryData: pd.DataFrame) -> pd.DataFrame:
    """
    Task 1.2: Distance-Domain Resampling
    
    FastF1 data is sampled based on time, meaning the distance between samples varies
    with the car's speed. To compare our simulator lap directly to the reference lap,
    we need to align them on a common X-axis: distance in meters.
    
    This function interpolates the raw telemetry so we get exactly one sample for
    every 1 meter of the track length.
    
    Args:
        telemetryData (pd.DataFrame): The raw telemetry DataFrame from FastF1.
        
    Returns:
        pd.DataFrame: A new DataFrame with distance uniformly spaced from 0 to lap length.
    """
    # Extract the distance array from the raw telemetry.
    # We create a mask for strictly increasing distances since interp1d requires a strictly monotonic x-axis.
    mask = telemetryData['Distance'].diff() > 0
    # The first row will be NaN after diff(), so we set it to True
    mask.iloc[0] = True
    
    cleanTelemetry = telemetryData[mask]
    originalDistance = cleanTelemetry['Distance'].values
    
    # Define our new equidistant distance array: 0, 1, 2, ..., Max Distance (1-meter intervals)
    maxDistance = int(np.floor(originalDistance[-1]))
    uniformDistance = np.arange(0, maxDistance + 1, 1) 
    
    resampledData = {'Distance': uniformDistance}
    
    # List of columns we want to interpolate
    columnsToInterpolate = ['Speed', 'Throttle', 'Brake', 'RPM', 'nGear', 'Time']
    
    for col in columnsToInterpolate:
        if col == 'Time':
            # FastF1 Time is a timedelta. We convert to seconds (float) for interpolation.
            originalValues = cleanTelemetry['Time'].dt.total_seconds().values
        else:
            originalValues = cleanTelemetry[col].values
            
        # Create a linear interpolation function for this specific column
        # fill_value="extrapolate" handles any tiny edge cases at the start/end of the lap
        interpFunction = interp1d(originalDistance, originalValues, kind='linear', fill_value="extrapolate")
        
        # Calculate the new values at our 1-meter intervals
        resampledValues = interpFunction(uniformDistance)
        
        if col == 'nGear':
            # Gears are discrete integers, so we round them after interpolation
            resampledData['nGear'] = np.round(resampledValues).astype(int)
        else:
            resampledData[col] = resampledValues
            
    # Convert back to a DataFrame for Task 1.3
    return pd.DataFrame(resampledData)

def ExportToParquet(df: pd.DataFrame, filename: str):
    """
    Task 1.3: Export telemetry to Parquet format.
    Downcasts data types to save memory and improve load speeds.
    """
    # Downcast types to save memory (telemetry rarely needs float64)
    df['Distance'] = df['Distance'].astype('int32')
    df['Speed'] = df['Speed'].astype('float32')
    df['Throttle'] = df['Throttle'].astype('int8')
    # Brake arrives on/off (0-1) from both sources; store it as 0-100% to match throttle
    df['Brake'] = np.round(df['Brake'] * 100).astype('int8')
    df['RPM'] = df['RPM'].astype('int32')
    df['nGear'] = df['nGear'].astype('int8')
    df['Time'] = df['Time'].astype('float32')
    
    # Export to Parquet using the PyArrow engine
    os.makedirs(os.path.dirname(filename) or '.', exist_ok=True)
    df.to_parquet(filename, engine='pyarrow', index=False)
    print(f"Successfully exported to {filename}")

def ReferencePath(Year: int, Track: str, Driver: str) -> str:
    """Location of a reference lap inside the library: references/<Year>/<Track>/<DRIVER>.parquet"""
    return os.path.join('references', str(Year), Track, f"{Driver.upper()}.parquet")

def ExportLap(telemetryData, Year: int, Track: str, Driver: str) -> bool:
    """Task 1.2 + 1.3: resample one lap to 1-meter intervals and save it to the library."""
    if telemetryData is None:
        return False
    resampledTelemetry = ResampleTelemetry(telemetryData)
    ExportToParquet(resampledTelemetry, ReferencePath(Year, Track, Driver))
    return True

def RunFastF1(args) -> tuple:
    session = LoadSession(args.year, args.track, args.session)
    trackName = session.event['Location']
    drivers = [args.driver.upper()] if args.driver else sorted(session.laps['Driver'].unique())

    print(f"\nExporting {len(drivers)} driver(s) for {args.year} {trackName}...")
    exported = sum(ExportLap(DownloadSessionData(session, d), args.year, trackName, d) for d in drivers)
    return exported, trackName

def RunOpenF1(args) -> tuple:
    session = openf1_source.FindSession(args.year, args.track, args.session)
    trackName = session['location']
    driverNumbers = openf1_source.GetDrivers(session)
    drivers = [args.driver.upper()] if args.driver else sorted(driverNumbers)

    print(f"Exporting {len(drivers)} driver(s) for {args.year} {trackName} ({session['session_name']}) from OpenF1...")
    exported = 0
    for driver in drivers:
        if driver not in driverNumbers:
            print(f"  {driver}: not in this session, skipping")
            continue
        lap = openf1_source.DownloadFastestLap(session, driverNumbers[driver])
        print(f"  {driver}: " + ("lap downloaded" if lap is not None else "no timed lap, skipping"))
        exported += ExportLap(lap, args.year, trackName, driver)
    return exported, trackName

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export fastest laps into the OpenATLAS reference library.")
    parser.add_argument('--year', type=int, default=2023, help="Championship year, e.g. 2023")
    parser.add_argument('--track', default='Monza', help="Grand Prix location, e.g. 'Monza' or 'Silverstone'")
    parser.add_argument('--session', default='Q', help="Session: FP1, FP2, FP3, Q, S, SQ, R (default Q)")
    parser.add_argument('--driver', default=None, help="Three-letter code, e.g. HAM. Omit to export every driver.")
    parser.add_argument('--source', choices=['openf1', 'fastf1'], default='openf1', help="Data source (default openf1)")
    args = parser.parse_args()

    # Task 1.1 Execution: Download the session once, then every requested driver
    exported, trackName = RunOpenF1(args) if args.source == 'openf1' else RunFastF1(args)

    print(f"\nDone. {exported} reference lap(s) saved under references/{args.year}/{trackName}/")
