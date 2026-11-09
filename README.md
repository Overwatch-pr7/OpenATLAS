# OpenATLAS

A lightweight desktop application similar to professional motorsport telemetry software (McLaren ATLAS, MoTeC i2 Pro) built with C# and .NET 10
OpenATLAS compares your racing simulator laps against real Formula 1 telemetry in real time, showing exactly where you gain or lose time -- meter by meter.

---

## What It Does

OpenATLAS operates on two data streams:

1. **Live Input** -- Reads real-time telemetry from **Assetto Corsa** through its shared memory at 60 Hz: car physics (speed, throttle, brake, gear, RPM, steering), lap/session state (lap time, laps completed, position on the lap, pit lane, pause) and session info (track, layout, car, driver).
2. **Reference Baseline** -- Real-world telemetry from actual F1 qualifying sessions (e.g., Hamilton at Monza), extracted with FastF1.

Both laps are placed on a common distance axis (1-meter intervals), aligned, and rendered as live trace comparisons with a continuous time delta readout.

---

## Architecture

```
extract_reference.py          Python pipeline (FastF1)
        |                     Outputs .parquet reference laps
        v
references/<Year>/<Grand Prix>/<DRIVER>.parquet
        |
        v
 OpenATLAS.Core (.NET 10)
 +-- Models/                  AcPhysicsPacket, AcGraphicsPacket, AcStaticInfo (Assetto Corsa memory pages),
 +                            TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
 +-- Network/                 SharedMemoryListener (reads all 3 AC pages at 60 Hz), UdpTelemetryListener (port 9996)
 +-- Collections/             CircularBuffer<T> (thread-safe, zero-allocation ring buffer)
 +-- Reference/               ReferenceLapIndex (Parquet reader, O(1) distance lookup, sub-meter interpolation)
 +                            ReferenceLibrary (discovers Year/Grand Prix/Driver hierarchy from disk)
 +-- Engine/                  LiveDeltaEngine (real-time delta calculation: Dt = t_ref(s) - t_sim(s))
        |
        v
 OpenATLAS.Workbench (WPF, .NET 10)
 +-- Themes/                  TracksideTheme.xaml (dark trackside color palette)
 +-- Plotting/                TracePanes (three ScottPlot panes: Speed, Pedals, Delta)
 +                            LiveRenderLoop (60 Hz UI loop that draws from the ring buffer)
 +-- ViewModels/              WorkbenchViewModel (orchestrates ingestion, delta engine, and UI state)
 +-- MainWindow.xaml          ATLAS-style dark-mode workbench shell

 OpenATLAS.ConsoleApp          Console harness: live delta, simulated dry-run, AC shared memory monitor
 OpenATLAS.Tests               xUnit test suite
```

### How live data flows

1. `SharedMemoryListener` polls Assetto Corsa's three memory pages on a background thread at 60 Hz. It only passes on a sample when AC has actually written a new one (packet id changed).
2. `LiveDeltaEngine` looks up the reference lap at the car's distance, computes the delta, and pushes the result into the `CircularBuffer` (10,000 samples, about 166 s at 60 Hz, so a full lap fits).
3. `LiveRenderLoop` runs on the UI thread at 60 frames per second. Each frame it copies the newest samples out of the buffer, updates the charts and readouts, and redraws once. The game never talks to the UI directly, so a burst of packets can't freeze the window.

Windows rounds timers to its ~15.6 ms clock tick, so a plain 16 ms timer only runs at ~31 Hz. The listener requests 1 ms timer resolution while it runs, and the render loop polls every 5 ms and paces frames with a stopwatch; both measure ~60 Hz.

---

## Technology Stack

| Layer | Technology |
|---|---|
| Reference Pipeline | Python 3, FastF1, NumPy, SciPy, Pandas, PyArrow |
| Core Engine | C# / .NET 10, `Parquet.Net`, `StructLayout` marshaling, `MemoryMappedFiles` |
| Desktop UI | WPF (.NET 10), ScottPlot 5, MVVM |
| Testing | xUnit |
| Data Format | Apache Parquet |
| License | GPL-3.0 |

---

## Getting Started

### Prerequisites

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Assetto Corsa (for live mode)
- Python 3.9+ (for reference data extraction only)

### 1. Clone the Repository

```bash
git clone https://github.com/<your-username>/OpenATLAS.git
cd OpenATLAS
```

### 2. Extract Reference Data

The repository already ships with the full 2021 season (see [Reference Data](#reference-data)). To extract more, install the Python dependencies:

```bash
pip install fastf1 numpy scipy pandas pyarrow
```

```bash
# Every driver's fastest qualifying lap, for every round of the 2021 season
python extract_reference.py --year 2021

# One Grand Prix
python extract_reference.py --year 2021 --track Monza

# One driver at one Grand Prix
python extract_reference.py --year 2021 --track Monza --driver HAM

# A different session (FP1, FP2, FP3, Q, S, SQ, R; default Q)
python extract_reference.py --year 2021 --track Spa --session R
```

Reference laps are saved to `references/<Year>/<Grand Prix>/<DRIVER>.parquet`. Laps already on disk are skipped, so an interrupted season download can simply be re-run.

### 3. Build and Run
I'll be releasing a packaged application soon don't worry :)
But for now->

```bash
dotnet build OpenATLAS.sln
dotnet run --project Workbench
```

The Workbench will launch and automatically load the first available reference lap from the `references/` folder.

### 4. Run Tests

```bash
dotnet test
```

---

## Usage

### Choosing the Two Racers

Every comparison has two laps, chosen in the bar under the header:

- **Racer 1** (cyan, the lap being analysed): **SOURCE** is `LIVE AC`, `SIMULATION`, or `SAVED LAP` (one of your own laps for the selected Grand Prix).
- **Racer 2** (yellow, the reference): **Year**, **Grand Prix** and **Driver**. Choose a season for a real F1 lap, or `My Laps` to compare against one of your own.

**START** (F5) runs Racer 1, **PAUSE** (F6) pauses a simulation or saved-lap replay, **STOP** (Shift+F5) ends it, and **RESET LAP** (Ctrl+R) clears the live traces, deltas and cursor pins. The same actions are in the **Session** menu.

### Live Mode (Assetto Corsa)

1. Select the reference lap for the track you are going to drive.
2. Set **Racer 1 SOURCE** to `LIVE AC` and click **START**. You can do this before or after starting the game: the app waits and connects on its own as soon as AC's shared memory appears.
3. Drive. Live traces are drawn in cyan over the yellow reference.

Current limitations (being worked on next):
- The live distance is estimated from speed over time, and the lap starts when you click Connect rather than at the start/finish line. AC's own lap position and lap timer are already being read (see the monitor below) and will replace this.
- Pausing the game and driving in the pit lane are not handled yet.

### Checking that AC data is read correctly (AC Shared Memory Monitor)

A live dashboard of every value OpenATLAS reads from the game, without the Workbench:

```bash
dotnet run --project ConsoleApp -- --ac-monitor
```

1. Start the monitor (it shows `WAITING FOR AC`).
2. Start Assetto Corsa and load any session (Practice / Hotlap is easiest). The monitor switches to `CONNECTED` and shows around 60 new samples per second.
3. Check each section:
   - **acpmf_static**: track name, layout, track length, car and your driver name match what you loaded.
   - **acpmf_graphics**: `Status` shows `Live` while driving and `Pause` in the Esc menu. `In pit lane` is `YES` in the pits. `Lap position` climbs from 0 to 1 around the lap (and the metres from 0 to the track length). `Laps done` goes up when you cross the line.
   - **acpmf_physics**: speed, gear (R / N / 1...), RPM, throttle, brake and clutch follow your inputs.
4. Press Enter to exit.

### Simulation Replay Mode
I built this just to test because I didn't feel like driving laps around Monza every time I wanted to test my code. This will be mostly be replaced in the end with say, a utility where you can simulate your own sim lap data but let's see.

For now, if the simulator is not running, set **Racer 1 SOURCE** to `SIMULATION` and click **START** to run a built-in dry-run. It creates a synthetic driver from the selected reference lap itself: 1.5% slower than the reference for the first 2000 m and 1% faster after that, with the reference's own pedal inputs and gears. This exercises the delta engine, trace rendering, and UI responsiveness with predictable results (about 0.5 s lost by 2000 m, recovered by the end of the lap). The replay currently plays back at roughly 60% of real time; the delta values are unaffected.

### Your Own Laps

Your laps are stored in `references/My Laps/<Grand Prix>/` (git-ignored) in the same format as the F1 laps, so they can be Racer 1 (`SAVED LAP`) or Racer 2 (`My Laps`). There are two ways to add them:

- **Record while driving:** click **REC** (or Session > Record Live AC Laps). Every complete lap driven in Assetto Corsa is saved under the selected Grand Prix. Laps follow AC's own lap counter and clock; out-laps, partial laps and laps you join midway are skipped.
- **Import a log:** **File > Import Lap from CSV** (Ctrl+O). Pick the file, check the detected columns, choose the lap (the fastest complete lap is preselected), the Grand Prix and a name.

Base Assetto Corsa does not record telemetry itself; common tools that do are [Telemetrick](https://overtake.gg/downloads/telemetrick.73265) (CSV or MoTeC export), Sim Racing Telemetry (Export to CSV) and MoTeC i2 (File > Export > CSV). The importer needs a header row with a **speed** column and a **time** and/or **distance** column; throttle, brake, gear, RPM and lap number are optional. Units are read from the header (`Speed [km/h]`, `(m/s)`, `mph`) or a MoTeC units row, and 0-1 pedal values are scaled to 0-100 %. See **Help > Import File Format** for the full description.

Imported and recorded laps are normalised like the reference laps: time and distance start at zero on the line and the lap is resampled to one sample per metre. When replayed against a reference, a lap is matched by fraction of the lap, so a sim track of slightly different length still lines up corner for corner.

---

## Project Structure

```
OpenATLAS/
|-- Core/                        Core telemetry engine library
|   |-- Collections/             CircularBuffer<T>
|   |-- Engine/                  LiveDeltaEngine
|   |-- Models/                  AC memory pages (AcPhysicsPacket, AcGraphicsPacket, AcStaticInfo, AcText),
|   |                            TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
|   |-- Network/                 SharedMemoryListener, UdpTelemetryListener
|   |-- Reference/               ReferenceLapIndex, ReferenceLibrary
|-- Workbench/                   WPF desktop application
|   |-- Plotting/                TracePanes (ScottPlot integration), LiveRenderLoop (60 Hz render loop)
|   |-- Themes/                  TracksideTheme.xaml
|   |-- ViewModels/              WorkbenchViewModel, ViewModelBase, RelayCommand
|   |-- MainWindow.xaml          Main application window
|-- ConsoleApp/                  Console harness and AC shared memory monitor (AcMonitor)
|-- Tests/                       xUnit test suite
|   |-- Data/                    Fixed reference lap used by the tests
|-- references/                  Extracted F1 reference laps (.parquet)
|-- extract_reference.py         Python reference data pipeline (FastF1)
|-- OpenATLAS.sln                Visual Studio solution
```

---

## Current Progress

### Completed

| Phase | Task | Description |
|---|---|---|
| 1 | 1.1 | FastF1 session downloader with CLI (whole season, one Grand Prix or one driver) |
| 1 | 1.2 | Distance-domain resampling (1-meter intervals, lap aligned to exactly t = 0 at the line) |
| 1 | 1.3 | Parquet export with a strict, downcasted schema |
| 2 | 2.1 | Assetto Corsa memory page structs (physics, graphics, static) matching AC's C layout byte-for-byte, locked by layout tests |
| 2 | 2.2 | `SharedMemoryListener`: reads all three AC pages at 60 Hz, waits for the game, skips repeated samples; `UdpTelemetryListener` |
| 2 | 2.3 | Zero-allocation struct reads via `MemoryMappedViewAccessor.Read` |
| 3 | 3.1 | Thread-safe `CircularBuffer<T>` with `Span<T>` bulk copy and a change counter |
| 3 | 3.2 | `ReferenceLapIndex` (Parquet-to-parallel-array loader, O(1) distance lookup, sub-meter interpolation) |
| 3 | 3.3 | `LiveDeltaEngine` (real-time delta: Dt = t_ref(s) - t_sim(s), lap crossover detection) |
| 4 | 4.1 | WPF dark-mode workbench shell with MVVM, trackside theme, and reference library dropdowns |
| 4 | 4.2 | ScottPlot integration: three linked trace panes (Speed, Pedals, Delta) with pre-allocated scatter buffers |
| 4 | 4.3 | Decoupled 60 Hz UI render loop pulling from the ring buffer instead of rendering on every packet |

### Remaining

| Phase | Task | Description |
|---|---|---|
| - | - | AC session tracking: lap distance and time from AC's own lap position and timer, automatic lap start at the line, pause and pit lane handling |
| - | - | Always-on-top, click-through delta overlay for use while driving |
| 5 | 5.1 | Head-up delta bar widget (large centered +/- seconds display with green/red coloring) |
| 5 | 5.2 | Corner analysis table (automated per-corner splits: apex speed, braking point, throttle pickup) |
| 5 | 5.3 | Session saver and exporter (save completed sim laps as `.parquet` / `.csv` to become future reference baselines) |

---

## Reference Data

The `references/` folder ships with the **full 2021 season**: every driver's fastest qualifying lap at all 22 Grands Prix. Folders are named by event, because some circuits hosted two races that year (e.g. the Styrian and Austrian GPs at the Red Bull Ring).

```
references/
|-- 2021/
|   |-- Abu Dhabi Grand Prix/
|   |   |-- ALO.parquet
|   |   |-- HAM.parquet
|   |   |-- ...
|   |   |-- track.json        Sector boundaries, corners and track map outline
|   |-- Austrian Grand Prix/
|   |-- ...
|   |-- United States Grand Prix/
```

A few drivers are missing at some events because they set no qualifying time (crashes, red flags, or not taking part).

Live comparisons only make sense on tracks that exist both in the reference data and in Assetto Corsa (official tracks such as Monza, Spa, Silverstone, Barcelona, Red Bull Ring and Imola, plus many more as community mods).

### How it works

- The Workbench reads only the `references/` folder. It needs no internet connection, no Python and no FastF1 at runtime; a fresh clone works as-is.
- Each `.parquet` file is one driver's fastest lap, resampled to one sample per metre (speed, throttle, brake, RPM, gear, elapsed time), so it lines up directly with the live car's distance.
- Each Grand Prix folder has a `track.json` with the three timing-sector boundaries, corner positions and a track map outline (X/Y every 10 m, from FastF1 position data), used by the sector analysis panel and the track map. Corner names can be added by hand in its `name` fields.
- The dropdowns are built from the folder structure, so any lap added to `references/` appears after restarting the app.
- `cache/` is FastF1's download cache. It is not part of the repository; `extract_reference.py` creates and fills it on first use.

### Getting any driver's lap

1. Install the dependencies: `pip install fastf1 numpy scipy pandas pyarrow`
2. From the repository root, run the extractor with the year, Grand Prix and driver code:
   ```bash
   python extract_reference.py --year 2022 --track Silverstone --driver NOR
   ```
   Omit `--driver` for the whole grid, omit `--track` for the whole season, and add `--session R` for race laps (fastest lap of the race).
3. Restart the Workbench and select the lap from the dropdowns.

The first download of a session takes a minute or two; repeat runs are served from `cache/`.

**Data availability:** without an F1 account, FastF1 serves timing data from a public mirror. As of November 2026 (tested with FastF1 3.8.3), the 2021 and 2022 seasons download without signing in; other seasons return no lap data and the extractor reports `no lap data available`. Those seasons may require signing in with an F1TV account (supported from FastF1 3.9).

Note that the library has no separate session level: race laps are saved to the same Grand Prix folder as qualifying laps, so a driver's file is skipped if one already exists there.

---

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
