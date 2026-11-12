# OpenATLAS

A lightweight desktop application similar to professional motorsport telemetry software (McLaren ATLAS, MoTeC i2 Pro) built with C# and .NET 10
OpenATLAS compares your racing simulator laps against real Formula 1 telemetry in real time, showing exactly where you gain or lose time -- meter by meter.

---

## What It Does

OpenATLAS compares two laps, distance by distance:

1. **Racer 1** -- the lap being analysed:
   - **Live** telemetry from **Assetto Corsa** through its shared memory at 60 Hz: car physics (speed, throttle, brake, gear, RPM, steering), lap/session state (lap time, laps completed, position on the lap, pit lane, pause) and session info (track, layout, car, driver).
   - One of **your own saved laps**, recorded live in the Workbench or imported from a telemetry log (CSV).
   - A built-in **simulated** lap for testing without the game.
2. **Racer 2** -- the reference: real-world telemetry from actual F1 qualifying sessions (e.g., Hamilton at Monza), extracted with FastF1, or another of your own laps.

Both laps are placed on a common distance axis (1-meter intervals), aligned, and rendered as trace comparisons with a continuous time delta, per-sector deltas, a distance cursor and a live track map.

---

## Architecture

```
extract_reference.py          Python pipeline (FastF1)
        |                     Outputs .parquet reference laps
        v
references/<Year>/<Grand Prix>/<DRIVER>.parquet + track.json (sectors, corners, map outline)
references/My Laps/<Grand Prix>/<lap>.parquet      (your recorded / imported laps)
        |
        v
 OpenATLAS.Core (.NET 10)
 +-- Models/                  AcPhysicsPacket, AcGraphicsPacket, AcStaticInfo (Assetto Corsa memory pages),
 +                            TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
 +-- Network/                 SharedMemoryListener (reads all 3 AC pages at 60 Hz), UdpTelemetryListener (port 9996)
 +-- Collections/             CircularBuffer<T> (thread-safe, zero-allocation ring buffer)
 +-- Reference/               ReferenceLapIndex (Parquet reader, O(1) distance lookup, sub-meter interpolation)
 +                            ReferenceLibrary (discovers Year/Grand Prix/Driver hierarchy, saves My Laps)
 +                            TrackLayout (track.json: sectors, corners, map outline)
 +-- Engine/                  LiveDeltaEngine (real-time delta calculation: Dt = t_ref(s) - t_sim(s))
 +                            PlaybackDrivers (simulated lap, saved lap replay)
 +-- Import/                  CsvLapImporter (telemetry logs), LapNormalizer (1 m resampling),
 +                            LapParquetWriter, LapRecorder (live AC laps)
        |
        v
 OpenATLAS.Workbench (WPF, .NET 10)
 +-- Themes/                  TracksideTheme.xaml (dark trackside color palette)
 +-- Plotting/                TracePanes (three ScottPlot panes: Speed, Pedals, Delta)
 +                            LiveRenderLoop (60 Hz UI loop that draws from the ring buffer)
 +                            DistanceCursor (hover readout, pins), TrackMap (circuit map, car position)
 +-- ViewModels/              WorkbenchViewModel (orchestrates ingestion, delta engine, and UI state),
 +                            SectorViewModel, CursorReadoutViewModel, ImportLapViewModel
 +-- MainWindow.xaml          ATLAS-style dark-mode workbench shell with menu bar
 +-- ImportLapWindow.xaml     File > Import Lap dialog

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

How laps are timed:
- Distance and lap time come from AC itself (its lap position and lap clock), with the AC lap mapped onto the reference lap by fraction, so a track of slightly different length still lines up corner for corner.
- The comparison starts when you cross the start/finish line. On an out lap, or after returning to the pits, the status bar says so and nothing is drawn until the line.
- Pausing the game stops the clock; the lap carries on when you resume.

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

- **Record while driving:** click **REC** (or Session > Record Live AC Laps). Every complete lap driven in Assetto Corsa, line to line, is captured and counted on the button (`REC ON (3)`); the status bar says when a lap is captured or why one was skipped (started mid-lap, pit lane, reset). Captured laps stay in memory until you press **SAVE** (File > Save Recorded Laps, Ctrl+S) to keep, name or discard them; closing the app with unsaved laps asks first. Laps follow AC's own lap counter and clock.
- **Import a log:** **File > Import Lap from CSV** (Ctrl+O). Pick the file, check the detected columns, choose the lap (the fastest complete lap is preselected), the Grand Prix and a name.

Base Assetto Corsa does not record telemetry itself; common tools that do are [Telemetrick](https://overtake.gg/downloads/telemetrick.73265) (CSV or MoTeC export), Sim Racing Telemetry (Export to CSV) and MoTeC i2 (File > Export > CSV). The importer needs a header row with a **speed** column and a **time** and/or **distance** column; throttle, brake, gear, RPM and lap number are optional. Units are read from the header (`Speed [km/h]`, `(m/s)`, `mph`) or a MoTeC units row, and 0-1 pedal values are scaled to 0-100 %. See **Help > Import File Format** for the full description.

Imported and recorded laps are normalised like the reference laps: time and distance start at zero on the line and the lap is resampled to one sample per metre. When replayed against a reference, a lap is matched by fraction of the lap, so a sim track of slightly different length still lines up corner for corner.

### Floating Widget (single-screen racing)

**View > Floating Widget** (Ctrl+Shift+F12) opens a small always-on-top panel to keep over the game: live delta, speed vs reference, gear, lap time and REC status, with **START/STOP**, **REC** and **SAVE LAP** (saves the last captured lap under its suggested name). Drag it by its header.

- It never takes focus from the game, so clicking it does not interrupt your driving inputs, and it is hidden from the taskbar and Alt+Tab.
- Assetto Corsa must run in **borderless or windowed** mode; no window can be drawn over an exclusive fullscreen game.
- Global hotkeys work while the game has focus (and can be mapped to wheel buttons with your wheel's software):

| Hotkey | Action |
|---|---|
| Ctrl+Shift+F9 | Start / stop Racer 1 |
| Ctrl+Shift+F10 | REC on / off |
| Ctrl+Shift+F11 | Save the last captured lap |
| Ctrl+Shift+F12 | Show / hide the widget |

### Corner Analysis

**CORNERS** (next to TRACK SECTOR ANALYSIS), **View > Corner Analysis** or **Ctrl+K** opens a table of every corner of the selected Grand Prix, Racer 1 against Racer 2:

| Column | Meaning |
|---|---|
| TIME | Time gained (+) or lost (-) between the corners either side |
| MIN SPEED | Slowest point through the corner, km/h |
| BRAKING POINT | Where braking starts (metres from the line). EARLY = you brake before Racer 2 |
| THROTTLE PICK-UP | Where the throttle passes 50 % on the way out. FLAT = never lifted, LIFTED = you lifted where Racer 2 stayed flat |
| FOCUS | The biggest difference in that corner (e.g. BRAKE LATER, CARRY MORE SPEED, EARLIER THROTTLE) |

The three corners losing the most time are marked in red and listed at the top. Racer 1 can be the lap in progress (refreshed every second while running), a lap captured with REC, or any saved lap; Racer 2 is whatever is selected in the main window. The window can stay open beside the graphs.

---

## Project Structure

```
OpenATLAS/
|-- Core/                        Core telemetry engine library
|   |-- Analysis/                CornerAnalysis (corner zones, apex speed, braking point, throttle pick-up)
|   |-- Collections/             CircularBuffer<T>
|   |-- Engine/                  LiveDeltaEngine, PlaybackDrivers
|   |-- Import/                  CsvLapImporter, LapNormalizer, LapParquetWriter, LapRecorder
|   |-- Models/                  AC memory pages (AcPhysicsPacket, AcGraphicsPacket, AcStaticInfo, AcText),
|   |                            TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
|   |-- Network/                 SharedMemoryListener, UdpTelemetryListener
|   |-- Reference/               ReferenceLapIndex, ReferenceLibrary, TrackLayout
|-- Workbench/                   WPF desktop application
|   |-- Assets/                  App icon (OpenATLAS.ico, all Windows sizes) and logo
|   |-- Plotting/                TracePanes (ScottPlot integration), LiveRenderLoop (60 Hz render loop),
|   |                            DistanceCursor, TrackMap
|   |-- Themes/                  TracksideTheme.xaml
|   |-- ViewModels/              WorkbenchViewModel, SectorViewModel, CursorReadoutViewModel, CornerAnalysisViewModel,
|   |                            CornerRowViewModel, ImportLapViewModel, SaveLapsViewModel, ViewModelBase, RelayCommand
|   |-- MainWindow.xaml          Main application window
|   |-- ImportLapWindow.xaml     File > Import Lap dialog
|   |-- InfoWindow.xaml          Help windows
|   |-- SaveLapsWindow.xaml      File > Save Recorded Laps dialog
|   |-- OverlayWindow.xaml       Floating widget shown over the game
|   |-- CornerAnalysisWindow.xaml  Corner analysis table
|-- ConsoleApp/                  Console harness and AC shared memory monitor (AcMonitor)
|-- Tests/                       xUnit test suite
|   |-- Data/                    Fixed reference lap used by the tests
|-- references/                  Extracted F1 reference laps (.parquet) and track.json per Grand Prix
|   |-- My Laps/                 Your recorded and imported laps (git-ignored)
|-- assets/                      Logo source artwork
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
| 1 | 1.3 | Parquet export with a strict, non-nullable schema; full 2021 season shipped (22 Grands Prix, 411 laps) |
| 1 | - | `track.json` per Grand Prix: timing-sector boundaries, corner positions, map outline and turn-number placement |
| 2 | 2.1 | Assetto Corsa memory page structs (physics, graphics, static) matching AC's C layout byte-for-byte, locked by layout tests |
| 2 | 2.2 | `SharedMemoryListener`: reads all three AC pages at 60 Hz, waits for the game, skips repeated samples; `UdpTelemetryListener` |
| 2 | 2.3 | Zero-allocation struct reads via `MemoryMappedViewAccessor.Read` |
| 3 | 3.1 | Thread-safe `CircularBuffer<T>` with `Span<T>` bulk copy and a change counter |
| 3 | 3.2 | `ReferenceLapIndex` (Parquet-to-parallel-array loader, O(1) distance lookup, sub-meter interpolation) |
| 3 | 3.3 | `LiveDeltaEngine` (real-time delta: Dt = t_ref(s) - t_sim(s), lap crossover detection) |
| 4 | 4.1 | WPF dark-mode workbench shell with MVVM, trackside theme, and reference library dropdowns (Year / Grand Prix / Driver) |
| 4 | 4.2 | ScottPlot integration: three linked trace panes (Speed, Pedals, Delta) with pre-allocated scatter buffers |
| 4 | 4.3 | Decoupled 60 Hz UI render loop pulling from the ring buffer instead of rendering on every packet |
| 5 | 5.1 | Head-up delta: large green/red live delta in the sidebar and in the floating widget |
| 5 | 5.2 | Corner analysis table: per-corner time, minimum speed, braking point and throttle pick-up vs Racer 2, with the biggest losses and a focus per corner |
| 5 | 5.3 | Your own laps: live AC lap recording (REC) and CSV log import (Telemetrick, Sim Racing Telemetry, MoTeC i2 exports), normalised and saved as `.parquet` to My Laps |
| - | - | AC lap tracking: live distance from AC's own lap position (`NormalizedCarPosition`) and lap clock, comparison starts at the start/finish line (out laps and returns to the pits are skipped), clock stops while paused |
| - | - | Delta pane rescales to the lap's delta (symmetric around zero), back to +/-1 s on a new lap |
| - | - | Pedal traces of saved laps and replays shown at their true value (a 1 % input is no longer drawn as 100 %) |
| - | - | Two-racer comparison: Racer 1 from live AC, simulation or a saved lap; Racer 2 from any F1 lap or your own |
| - | - | Track sector analysis per Grand Prix: reference sector times and live per-sector deltas |
| - | - | Distance cursor across all three traces: hover readout of live vs reference values, pins with snapping and red markers |
| - | - | Track map: circuit outline in official orientation, sector highlighting, turn numbers, car / cursor / pin markers; per-track sizing and hide/show |
| - | - | Menu bar (File / Session / View / Help) with keyboard shortcuts; START / PAUSE / STOP / RESET controls; data source indicator; scrollable sidebar |
| - | - | Simulation and saved-lap replay with pause/resume |
| - | - | REC captures complete laps in memory with reasons for skipped laps; SAVE dialog (save / save all / discard) and unsaved-lap prompt on exit |
| - | - | Floating widget over the game (translucent, never takes focus) with live delta, speed, gear, lap time, REC status and controls; global hotkeys |

### Remaining

| Phase | Task | Description |
|---|---|---|
| - | - | In-game validation of live recording (REC) and of CSV import with real Telemetrick / Sim Racing Telemetry / MoTeC exports |
| - | - | v1 release: self-contained Windows build and installer (e.g. Inno Setup), My Laps moved to the user's Documents folder (Program Files is read-only), version number and app icon |
| - | - | Release decisions: code signing (Windows SmartScreen warning) and the terms for redistributing F1 timing data |
| 5 | 5.3 | Optional: export laps as `.csv` (Parquet saving is done) |
| 5 | - | Phase 5 deliverable: architecture diagrams, performance benchmarks (CPU / allocations) and a demo video of a sim lap over an F1 trace |
| - | - | Optional: direct MoTeC `.ld` import; corner names in `track.json`; more seasons via FastF1 with an F1TV sign-in (only 2021-2022 download without one); comparing more than two laps |

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
