"""
OpenATLAS - Phase 1: Reference Data Pipeline
Task 1.1: FastF1 Session Downloader

This script uses the `fastf1` library to download real-world telemetry from
official F1 sessions. It serves as the baseline data for the OpenATLAS telemetry tool.
Historical sessions are public: no F1 account or login is needed.

--- HELP BLOCK: Selecting Races ---
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
-----------------------------------

--- Reference Library (used by the Workbench dropdowns) ---
Laps are saved as: references/<Year>/<Grand Prix>/<DRIVER>.parquet
(e.g. references/2021/Italian Grand Prix/HAM.parquet). One file per driver: their fastest lap.
Each Grand Prix folder also gets a track.json with the sector boundaries, corner numbers and
track map outline used by the Workbench. Corner names can be typed into its "name" fields by hand.
The event name is used rather than the circuit because some circuits host two events
in a season (2021: Styrian and Austrian Grand Prix, both at Spielberg).

Command line:
python extract_reference.py --year 2021                              # every round, every driver
python extract_reference.py --year 2021 --track Monza                # one Grand Prix, every driver
python extract_reference.py --year 2021 --track Monza --driver HAM   # one driver
python extract_reference.py --year 2021 --session R                  # race laps instead of qualifying

Laps that already exist are skipped, so an interrupted season download can simply be re-run.
-----------------------------------------------------------
"""

import os
import json
import time
import argparse
import fastf1
import numpy as np
import pandas as pd
import pyarrow as pa
import pyarrow.parquet as pq
from scipy.interpolate import interp1d

CACHE_DIR = 'cache'
REFERENCE_DIR = 'references'
TRACK_INFO_FILE = 'track.json'

# Pause between sessions so the timing API is never hammered during a season download
SESSION_DELAY_S = 3

# Shorter than any F1 circuit: anything below this is a broken or partial lap
MIN_LAP_LENGTH_M = 3000

# Explicit non-nullable schema: the C# reader (ReferenceLapIndex) reads these columns
# raw with these exact types, and non-nullable columns carry no definition levels
REFERENCE_SCHEMA = pa.schema([
    pa.field('Distance', pa.int32(), nullable=False),
    pa.field('Speed', pa.float32(), nullable=False),
    pa.field('Throttle', pa.int8(), nullable=False),
    pa.field('Brake', pa.int8(), nullable=False),
    pa.field('RPM', pa.int32(), nullable=False),
    pa.field('nGear', pa.int8(), nullable=False),
    pa.field('Time', pa.float32(), nullable=False),
])

def LoadSession(Year: int, GrandPrix, SessionType: str):
    """
    Downloads and loads a session. One load contains every driver's laps,
    so exporting the whole grid costs a single download.

    Args:
        Year (int): The year of the race (e.g., 2021).
        GrandPrix (str | int): The name/location of the Grand Prix (e.g., 'Monza') or its round number.
        SessionType (str): The session identifier (e.g., 'Q' for Qualifying).
    """
    session = fastf1.get_session(Year, GrandPrix, SessionType)

    # Only laps and car telemetry are needed for a reference lap
    session.load(laps=True, telemetry=True, weather=False, messages=False)
    return session

def DownloadSessionData(session, Driver: str):
    """
    Extracts the fastest lap of a driver from a loaded session as car telemetry
    with a Distance channel, aligned so Time = 0 s and Distance = 0 m on the start/finish line.

    Args:
        session: A session returned by LoadSession.
        Driver (str): The three-letter abbreviation of the driver (e.g., 'HAM' for Lewis Hamilton).
    """
    try:
        driverLap = session.laps.pick_drivers(Driver).pick_fastest()
        if driverLap is None or driverLap.empty or pd.isna(driverLap['LapTime']):
            print(f"    {Driver}: no timed lap, skipping")
            return None

        lapTime = driverLap['LapTime'].total_seconds()

        # Car data is ~4 Hz, so the first sample lands a fraction of a second after the line.
        # Pad by 1 s on both sides, integrate speed into distance, then interpolate exact
        # rows at the start and finish line.
        telemetry = driverLap.get_car_data(pad=1, pad_side='both').add_distance()
        t = telemetry['Time'].dt.total_seconds().values
        distance = telemetry['Distance'].values - np.interp(0.0, t, telemetry['Distance'].values)

        lapTimes = np.concatenate([[0.0], t[(t > 0) & (t < lapTime)], [lapTime]])

        # The start row is the line by definition; pin it so float error cannot put it a hair past 0 m
        lapDistance = np.interp(lapTimes, t, distance)
        lapDistance[0] = 0.0

        return pd.DataFrame({
            'Distance': lapDistance,
            'Speed': np.interp(lapTimes, t, telemetry['Speed'].values),
            'Throttle': np.interp(lapTimes, t, telemetry['Throttle'].values),
            'Brake': np.interp(lapTimes, t, telemetry['Brake'].astype(float).values),
            'RPM': np.interp(lapTimes, t, telemetry['RPM'].values),
            'nGear': np.interp(lapTimes, t, telemetry['nGear'].values),
            'Time': pd.to_timedelta(lapTimes, unit='s'),
        })

    except Exception as e:
        print(f"    {Driver}: an error occurred while extracting the lap: {e}")
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
        telemetryData (pd.DataFrame): Lap telemetry with Distance, Speed, Throttle, Brake, RPM, nGear, Time.

    Returns:
        pd.DataFrame: A new DataFrame with distance uniformly spaced from 0 to lap length.
    """
    # interp1d requires a strictly increasing x-axis, so drop samples where the car did not move
    mask = telemetryData['Distance'].diff() > 0
    # The first row will be NaN after diff(), so we set it to True
    mask.iloc[0] = True

    cleanTelemetry = telemetryData[mask].dropna()
    originalDistance = cleanTelemetry['Distance'].values

    # Define our new equidistant distance array: 0, 1, 2, ..., Max Distance (1-meter intervals)
    maxDistance = int(np.floor(originalDistance[-1]))
    uniformDistance = np.arange(0, maxDistance + 1, 1)

    resampledData = {'Distance': uniformDistance}

    # Continuous channels interpolate linearly; discrete ones must not be blended:
    # brake is on/off (nearest sample), gear holds until the next shift (previous sample)
    interpolationKind = {
        'Speed': 'linear', 'Throttle': 'linear', 'RPM': 'linear', 'Time': 'linear',
        'Brake': 'nearest', 'nGear': 'previous',
    }

    for col, kind in interpolationKind.items():
        if col == 'Time':
            # FastF1 Time is a timedelta. We convert to seconds (float) for interpolation.
            originalValues = cleanTelemetry['Time'].dt.total_seconds().values
        else:
            originalValues = cleanTelemetry[col].values.astype(float)

        # fill_value="extrapolate" handles any tiny edge cases at the start/end of the lap
        interpFunction = interp1d(originalDistance, originalValues, kind=kind, fill_value="extrapolate")
        resampledData[col] = interpFunction(uniformDistance)

    return pd.DataFrame(resampledData)

def ExportToParquet(df: pd.DataFrame, filename: str):
    """
    Task 1.3: Export telemetry to Parquet format.
    Downcasts data types to save memory and improve load speeds.
    """
    df = pd.DataFrame({
        'Distance': df['Distance'].astype('int32'),
        'Speed': df['Speed'].astype('float32'),
        'Throttle': np.clip(np.round(df['Throttle']), 0, 100).astype('int8'),
        # FastF1 brake is on/off (0-1); store it as 0-100% to match throttle
        'Brake': np.clip(np.round(df['Brake'] * 100), 0, 100).astype('int8'),
        'RPM': np.round(df['RPM']).astype('int32'),
        'nGear': np.round(df['nGear']).astype('int8'),
        'Time': df['Time'].astype('float32'),
    })

    os.makedirs(os.path.dirname(filename) or '.', exist_ok=True)
    table = pa.Table.from_pandas(df, schema=REFERENCE_SCHEMA, preserve_index=False)
    pq.write_table(table, filename, compression='snappy')

def ReferencePath(Year: int, GrandPrix: str, Driver: str) -> str:
    """Location of a reference lap inside the library: references/<Year>/<Grand Prix>/<DRIVER>.parquet"""
    return os.path.join(REFERENCE_DIR, str(Year), GrandPrix, f"{Driver.upper()}.parquet")

def TrackInfoPath(Year: int, GrandPrix: str) -> str:
    """Track layout (sectors + corners) shared by every lap of a Grand Prix: references/<Year>/<Grand Prix>/track.json"""
    return os.path.join(REFERENCE_DIR, str(Year), GrandPrix, TRACK_INFO_FILE)

# Spacing of the track map outline points stored in track.json
OUTLINE_STEP_M = 10

# How far a turn number is drawn from its corner on the track map (FastF1's own map examples use 50 m)
CORNER_LABEL_OFFSET_M = 50

def RotateToMap(xy: np.ndarray, rotationDegrees: float) -> np.ndarray:
    """Rotates FastF1 X/Y positions into the circuit's official map orientation."""
    angle = np.deg2rad(rotationDegrees)
    rotation = np.array([[np.cos(angle), np.sin(angle)], [-np.sin(angle), np.cos(angle)]])
    return xy @ rotation

def CornerLabelPositions(circuitInfo, rotationDegrees: float) -> dict:
    """
    Map position (metres, rotated) for each turn number, keyed by (number, letter).
    FastF1's corner 'Angle' gives the side of the track the label belongs on, so labels sit
    beside their corner instead of on top of the track, even where the circuit folds back on itself.
    """
    labels = {}
    for _, c in circuitInfo.corners.iterrows():
        angle = np.deg2rad(c['Angle'])
        offset = np.array([CORNER_LABEL_OFFSET_M, 0.0]) @ np.array([[np.cos(angle), np.sin(angle)], [-np.sin(angle), np.cos(angle)]])
        position = RotateToMap(np.array([c['X'] / 10.0 + offset[0], c['Y'] / 10.0 + offset[1]]), rotationDegrees)
        labels[(int(c['Number']), str(c['Letter'] or ''))] = (round(float(position[0]), 1), round(float(position[1]), 1))
    return labels

def TrackOutline(session, lap, rotationDegrees: float) -> list:
    """
    Track map outline from a lap's X/Y position data: [distance_m, x_m, y_m] every
    OUTLINE_STEP_M metres. FastF1 positions are in 1/10 m; they are converted to metres and
    rotated by the circuit's official map rotation (the same orientation as F1 TV graphics).
    """
    telemetry = lap.get_telemetry()
    telemetry = telemetry[telemetry['Distance'].diff().fillna(1) > 0]  # interp needs increasing distance

    distance = telemetry['Distance'].values
    xy = telemetry[['X', 'Y']].values / 10.0

    xy = RotateToMap(xy, rotationDegrees)

    steps = np.arange(0, distance[-1], OUTLINE_STEP_M)
    x = np.interp(steps, distance, xy[:, 0])
    y = np.interp(steps, distance, xy[:, 1])
    return [[round(float(d), 1), round(float(px), 1), round(float(py), 1)] for d, px, py in zip(steps, x, y)]

def ExportTrackInfo(session, Year: int, eventName: str):
    """
    Writes track.json for the Workbench sector analysis and track map:
      - sectors: start/end distance (m) of the three official timing sectors
      - corners: turn numbers, their distance (m) along the lap and where the number is drawn
                 on the map (labelX/labelY); 'name' is left empty for hand-written corner
                 names (e.g. "Prima Variante")
      - outline: [distance_m, x_m, y_m] points for the track map (see TrackOutline)

    FastF1 only gives sector *times*, so the boundaries are found by looking up those
    times on the fastest lap's own distance trace.

    An existing track.json missing the map data (outline or corner label positions) gets it
    added; everything else in it, including hand-written corner names, is kept.
    """
    path = TrackInfoPath(Year, eventName)
    existing = None
    if os.path.exists(path):
        with open(path, encoding='utf-8') as f:
            existing = json.load(f)
        if 'outline' in existing and all('labelX' in c for c in existing.get('corners', [])):
            return

    # Fastest lap that has all three sector times (some laps miss one, e.g. after a red flag)
    timedLaps = session.laps.dropna(subset=['LapTime', 'Sector1Time', 'Sector2Time', 'Sector3Time'])
    if timedLaps.empty:
        print("    no lap with all sector times, track.json not written")
        return
    lap = timedLaps.sort_values('LapTime').iloc[0]

    try:
        circuitInfo = session.get_circuit_info()
    except Exception as e:
        # Sectors and the outline are still useful without corner numbers
        print(f"    circuit info unavailable ({e}), no corners and no map rotation")
        circuitInfo = None
    rotationDegrees = float(circuitInfo.rotation) if circuitInfo is not None else 0.0

    try:
        outline = TrackOutline(session, lap, rotationDegrees)
    except Exception as e:
        print(f"    position data unavailable ({e}), no track map outline")
        outline = []

    labels = CornerLabelPositions(circuitInfo, rotationDegrees) if circuitInfo is not None else {}

    if existing is not None:
        existing['mapRotationDegrees'] = rotationDegrees
        if not existing.get('outline'):
            existing['outline'] = outline
        for corner in existing.get('corners', []):
            label = labels.get((corner['number'], corner.get('letter', '')))
            if label is not None:
                corner['labelX'], corner['labelY'] = label
        trackInfo = existing
    else:
        telemetry = DownloadSessionData(session, lap['Driver'])
        if telemetry is None:
            return
        lapTime = telemetry['Time'].dt.total_seconds().values
        distance = telemetry['Distance'].values

        sector1End = lap['Sector1Time'].total_seconds()
        sector2End = sector1End + lap['Sector2Time'].total_seconds()
        boundaries = [0.0,
                      float(np.interp(sector1End, lapTime, distance)),
                      float(np.interp(sector2End, lapTime, distance)),
                      float(distance[-1])]

        cornerList = []
        if circuitInfo is not None:
            cornerList = []
            for _, c in circuitInfo.corners.sort_values('Distance').iterrows():
                key = (int(c['Number']), str(c['Letter'] or ''))
                cornerList.append({'number': key[0], 'letter': key[1],
                                   'distanceMeters': round(float(c['Distance']), 1), 'name': '',
                                   'labelX': labels[key][0], 'labelY': labels[key][1]})

        trackInfo = {
            'event': eventName,
            'location': session.event['Location'],
            'lengthMeters': round(boundaries[-1], 1),
            'sectors': [{'number': i + 1,
                         'startMeters': round(boundaries[i], 1),
                         'endMeters': round(boundaries[i + 1], 1)} for i in range(3)],
            'corners': cornerList,
            'mapRotationDegrees': rotationDegrees,
            'outline': outline,
        }

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as f:
        # One outline point per line keeps the file readable without thousands of lines
        text = json.dumps({k: v for k, v in trackInfo.items() if k != 'outline'}, indent=2, ensure_ascii=False)
        points = ',\n    '.join(json.dumps(pt) for pt in trackInfo['outline'])
        f.write(text[:-2] + ',\n  "outline": [\n    ' + points + '\n  ]\n}\n')
    print(f"    track.json {'updated' if existing is not None else 'written'} "
          f"({len(trackInfo['corners'])} corners, {len(outline)} map points)")

def ExportSession(Year: int, GrandPrix, SessionType: str, Driver: str = None) -> int:
    """Downloads one session and exports the fastest lap of every requested driver. Returns laps written."""
    session = LoadSession(Year, GrandPrix, SessionType)
    eventName = session.event['EventName']

    # Without laps the session either was not run or its timing data is unavailable
    try:
        laps = session.laps
    except fastf1.exceptions.DataNotLoadedError:
        laps = pd.DataFrame()
    if laps.empty:
        print(f"    no lap data available for {eventName} ({SessionType}), skipping")
        return 0

    ExportTrackInfo(session, Year, eventName)

    drivers = [Driver.upper()] if Driver else sorted(laps['Driver'].dropna().unique())
    exported = 0
    for driver in drivers:
        path = ReferencePath(Year, eventName, driver)
        if os.path.exists(path):
            continue

        telemetryData = DownloadSessionData(session, driver)
        if telemetryData is None:
            continue

        # Task 1.2 Execution: Resample to 1-meter intervals
        resampledTelemetry = ResampleTelemetry(telemetryData)
        if resampledTelemetry['Distance'].iloc[-1] < MIN_LAP_LENGTH_M:
            print(f"    {driver}: lap only {resampledTelemetry['Distance'].iloc[-1]} m long, skipping")
            continue

        # Task 1.3 Execution: Export to Parquet
        ExportToParquet(resampledTelemetry, path)
        exported += 1

    print(f"    {exported} new lap(s) saved to {os.path.join(REFERENCE_DIR, str(Year), eventName)}")
    return exported

def ExportSeason(Year: int, SessionType: str, Driver: str = None) -> int:
    """Exports every Grand Prix of a season, round by round. Returns laps written."""
    schedule = fastf1.get_event_schedule(Year, include_testing=False)
    print(f"{Year}: {len(schedule)} rounds\n")

    exported = 0
    for _, event in schedule.iterrows():
        roundNumber = int(event['RoundNumber'])
        print(f"=== Round {roundNumber:02d}: {event['EventName']} ===")
        try:
            exported += ExportSession(Year, roundNumber, SessionType, Driver)
        except Exception as e:
            # One failed round must not stop the rest of the season
            print(f"    [ERROR] {e}")
        time.sleep(SESSION_DELAY_S)

    return exported

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export FastF1 fastest laps into the OpenATLAS reference library.")
    parser.add_argument('--year', type=int, default=2021, help="Championship year (default 2021)")
    parser.add_argument('--track', default=None, help="Grand Prix name or location, e.g. 'Monza'. Omit for the whole season.")
    parser.add_argument('--session', default='Q', help="Session: FP1, FP2, FP3, Q, S, SQ, R (default Q)")
    parser.add_argument('--driver', default=None, help="Three-letter code, e.g. HAM. Omit to export every driver.")
    args = parser.parse_args()

    # Enable cache to speed up subsequent requests and prevent redundant API calls
    os.makedirs(CACHE_DIR, exist_ok=True)
    fastf1.Cache.enable_cache(CACHE_DIR)
    fastf1.set_log_level('WARNING')

    # Task 1.1 Execution: Download the requested session(s)
    if args.track:
        total = ExportSession(args.year, args.track, args.session, args.driver)
    else:
        total = ExportSeason(args.year, args.session, args.driver)

    print(f"\nDone. {total} new reference lap(s) saved under {REFERENCE_DIR}/{args.year}/")
