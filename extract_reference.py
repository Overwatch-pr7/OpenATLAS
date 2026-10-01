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

if __name__ == "__main__":
    # Task 1.1 Execution: Download Lewis Hamilton's 2023 Italian GP qualifying lap
    # Parameters: Year=2023, Location='Monza', Session='Q', Driver='HAM'
    telemetryData = DownloadSessionData(2023, 'Monza', 'Q', 'HAM')
    
    if telemetryData is not None:
        print("\nRaw telemetry downloaded successfully.")
