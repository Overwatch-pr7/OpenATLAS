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
"""

import os
import fastf1
import numpy as np
import pandas as pd
from scipy.interpolate import interp1d

def DownloadSessionData(Year: int, GrandPrix: str, SessionType: str, Driver: str):
    """
    Downloads and loads the session data and extracts the fastest lap for a specific driver.
    
    Args:
        Year (int): The year of the race (e.g., 2023).
        GrandPrix (str): The name or location of the Grand Prix (e.g., 'Monza').
        SessionType (str): The session identifier (e.g., 'Q' for Qualifying).
        Driver (str): The three-letter abbreviation of the driver (e.g., 'HAM' for Lewis Hamilton).
    """
    print(f"Loading session: {Year} {GrandPrix} - {SessionType}")
    
    # Enable cache to speed up subsequent requests and prevent redundant API calls
    # We create the cache directory automatically if it doesn't exist
    os.makedirs('cache', exist_ok=True)
    fastf1.Cache.enable_cache('cache') 

    try:
        # Fetch the session from the FastF1 API
        session = fastf1.get_session(Year, GrandPrix, SessionType)
        
        # Load the data (this downloads the telemetry, timing, and weather data)
        session.load()
        
        print(f"Session loaded successfully. Extracting lap for driver: {Driver}")
        
        # Pick the fastest lap for the specified driver
        driverLap = session.laps.pick_drivers(Driver).pick_fastest()
        
        # Get the raw telemetry data (Speed, Throttle, Brake, RPM, Gear, etc.)
        telemetry = driverLap.get_telemetry()
        
        print(f"Telemetry extracted successfully. Rows of data: {len(telemetry)}")
        
        # Note: Task 1.2 and 1.3 will handle resampling and exporting this data.
        return telemetry
        
    except Exception as e:
        print(f"An error occurred while fetching the data: {e}")
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
    df['Brake'] = df['Brake'].astype('int8')
    df['RPM'] = df['RPM'].astype('int32')
    df['nGear'] = df['nGear'].astype('int8')
    df['Time'] = df['Time'].astype('float32')
    
    # Export to Parquet using the PyArrow engine
    df.to_parquet(filename, engine='pyarrow', index=False)
    print(f"Successfully exported to {filename}")

if __name__ == "__main__":
    # Task 1.1 Execution: Download Lewis Hamilton's 2023 Italian GP qualifying lap
    # Parameters: Year=2023, Location='Monza', Session='Q', Driver='HAM'
    telemetryData = DownloadSessionData(2023, 'Monza', 'Q', 'HAM')
    
    if telemetryData is not None:
        print(f"\nTask 1.1 Complete: Raw telemetry downloaded successfully. (Rows: {len(telemetryData)})")
        
        # Task 1.2 Execution: Resample to 1-meter intervals
        resampledTelemetry = ResampleTelemetry(telemetryData)
        
        print("\nTask 1.2 Complete: Telemetry resampled to equidistant 1-meter intervals.")
        print(f"Resampled Rows: {len(resampledTelemetry)}")
        print("\n--- Test Verification: Sample of resampled data (Distance 600m to 605m) ---")
        
        # Test validation to confirm it worked according to the plan
        testDistanceRange = resampledTelemetry[
            (resampledTelemetry['Distance'] >= 600) & (resampledTelemetry['Distance'] <= 605)
        ]
        print(testDistanceRange[['Distance', 'Speed', 'Brake', 'nGear']].to_string(index=False))
        
        # Task 1.3 Execution: Export to Parquet
        print("\nStarting Task 1.3: Exporting to Parquet...")
        ExportToParquet(resampledTelemetry, 'monza_reference_lap.parquet')
