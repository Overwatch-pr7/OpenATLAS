"""
OpenATLAS - Phase 1: Reference Data Pipeline (OpenF1 source)

Downloads fastest laps from the free OpenF1 API (https://openf1.org, 2023 season onward)
and returns them in the same shape FastF1 does, so ResampleTelemetry() and
ExportToParquet() in extract_reference.py work unchanged.

OpenF1 car data is time-based (~4 Hz) and has no distance channel. Like FastF1's
add_distance(), we integrate speed over time to get distance travelled.
"""

import time
import numpy as np
import pandas as pd
import requests

API = "https://api.openf1.org/v1"

# extract_reference.py session codes -> OpenF1 session_name
SESSION_NAMES = {
    'FP1': 'Practice 1', 'FP2': 'Practice 2', 'FP3': 'Practice 3',
    'Q': 'Qualifying', 'R': 'Race', 'S': 'Sprint',
    'SQ': 'Sprint Qualifying',
}

# Free tier allows 30 requests/minute, so requests are spaced 2 s apart
REQUEST_INTERVAL_S = 2.0
_lastRequest = 0.0

def Get(endpoint: str, query: str) -> list:
    """GET an OpenF1 endpoint, pacing requests and backing off when the rate limit is hit."""
    global _lastRequest
    for attempt in range(6):
        wait = _lastRequest + REQUEST_INTERVAL_S - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        _lastRequest = time.monotonic()

        response = requests.get(f"{API}/{endpoint}?{query}", timeout=30)
        if response.status_code == 429:
            delay = int(response.headers.get('Retry-After', 15 * (attempt + 1)))
            print(f"  (rate limited, waiting {delay}s)")
            time.sleep(delay)
            continue
        response.raise_for_status()
        return response.json()
    raise RuntimeError(f"OpenF1 rate limit: gave up on {endpoint}")

def FindSession(Year: int, Track: str, SessionType: str) -> dict:
    """
    Finds the session for Track (case-insensitive): an exact location, circuit or country
    name wins, otherwise the start of a location/circuit name ('Spa' -> 'Spa-Francorchamps').
    """
    sessionName = SESSION_NAMES.get(SessionType.upper(), SessionType)
    sessions = Get('sessions', f"year={Year}&session_name={sessionName}")

    track = Track.lower()
    for s in sessions:
        names = (s.get('location'), s.get('circuit_short_name'), s.get('country_name'))
        if any(n and n.lower() == track for n in names):
            return s
    for s in sessions:
        names = (s.get('location'), s.get('circuit_short_name'))
        if any(n and n.lower().startswith(track) for n in names):
            return s

    available = ', '.join(sorted({s['location'] for s in sessions}))
    raise SystemExit(f"No {Year} {sessionName} session found for '{Track}'. Available: {available}")

def GetDrivers(session: dict) -> dict:
    """Maps three-letter driver code -> car number for the session."""
    drivers = Get('drivers', f"session_key={session['session_key']}")
    return {d['name_acronym']: d['driver_number'] for d in drivers}

def DownloadFastestLap(session: dict, driverNumber: int):
    """
    Returns the driver's fastest lap as a DataFrame with the FastF1 columns
    ResampleTelemetry() expects: Distance, Speed, Throttle, Brake (0-1), RPM, nGear, Time (timedelta).
    """
    laps = Get('laps', f"session_key={session['session_key']}&driver_number={driverNumber}")
    timedLaps = [l for l in laps if l.get('lap_duration') and l.get('date_start') and not l.get('is_pit_out_lap')]
    if not timedLaps:
        return None

    best = min(timedLaps, key=lambda l: l['lap_duration'])
    lapStart = pd.Timestamp(best['date_start'])
    duration = best['lap_duration']

    # Pad the window so the lap's first and last metre can be interpolated
    windowStart = (lapStart - pd.Timedelta(seconds=2)).strftime('%Y-%m-%dT%H:%M:%S.%f')
    windowEnd = (lapStart + pd.Timedelta(seconds=duration + 2)).strftime('%Y-%m-%dT%H:%M:%S.%f')
    samples = Get('car_data', f"session_key={session['session_key']}&driver_number={driverNumber}"
                              f"&date>={windowStart}&date<={windowEnd}")
    if len(samples) < 10:
        return None

    raw = pd.DataFrame(samples)
    t = (pd.to_datetime(raw['date'], format='ISO8601') - lapStart).dt.total_seconds().values

    # Distance = integral of speed over time (trapezoid rule), km/h -> m/s
    speedMps = raw['speed'].values / 3.6
    distance = np.concatenate([[0.0], np.cumsum(np.diff(t) * (speedMps[1:] + speedMps[:-1]) / 2)])

    # Keep the samples inside the lap, plus exact rows at the start and finish line
    lapTimes = np.concatenate([[0.0], t[(t > 0) & (t < duration)], [duration]])

    def At(values):
        return np.interp(lapTimes, t, values)

    lapStartDistance = np.interp(0.0, t, distance)
    return pd.DataFrame({
        'Distance': At(distance) - lapStartDistance,
        'Speed': At(raw['speed'].values),
        'Throttle': np.clip(At(raw['throttle'].values), 0, 100),
        'Brake': np.clip(At(raw['brake'].values), 0, 100) / 100,  # OpenF1 is 0/100, FastF1 is 0/1
        'RPM': At(raw['rpm'].values),
        'nGear': At(raw['n_gear'].values),
        'Time': pd.to_timedelta(lapTimes, unit='s'),
    })
