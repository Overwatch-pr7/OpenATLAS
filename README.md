# OpenATLAS

A lightweight desktop application similar to professional motorsport telemetry software (McLaren ATLAS, MoTeC i2 Pro) built with C# and .NET 10
OpenATLAS compares your racing simulator laps against real Formula 1 telemetry in real time, showing exactly where you gain or lose time -- meter by meter.

---

## What It Does

OpenATLAS operates on two data streams:

1. **Live Input** -- Streams real-time telemetry (speed, throttle, brake, RPM, gear, steering) from Assetto Corsa / ACC via shared memory at 60 Hz.
2. **Reference Baseline** -- Real-world telemetry from actual F1 sessions (e.g., Hamilton at Monza, Leclerc at Silverstone), extracted via FastF1 or the OpenF1 API.

Both laps are resampled onto a common distance axis (1-meter intervals), aligned, and rendered as live trace comparisons with a continuous time delta readout.

---

## Architecture

```
extract_reference.py          Python pipeline (FastF1 / OpenF1)
        |                     Outputs .parquet reference laps
        v
references/<Year>/<Track>/<DRIVER>.parquet
        |
        v
 OpenATLAS.Core (.NET 10)
 +-- Models/                  AcPhysicsPacket, TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
 +-- Network/                 SharedMemoryListener (acpmf_physics), UdpTelemetryListener (port 9996)
 +-- Collections/             CircularBuffer<T> (thread-safe, zero-allocation ring buffer)
 +-- Reference/               ReferenceLapIndex (Parquet reader, O(1) distance lookup, sub-meter interpolation)
 +                            ReferenceLibrary (discovers Year/Track/Driver hierarchy from disk)
 +-- Engine/                  LiveDeltaEngine (real-time delta calculation: Dt = t_ref(s) - t_sim(s))
        |
        v
 OpenATLAS.Workbench (WPF, .NET 10)
 +-- Themes/                  TracksideTheme.xaml (dark trackside color palette)
 +-- Plotting/                TracePanes.cs (three ScottPlot panes: Speed, Pedals, Delta)
 +-- ViewModels/              WorkbenchViewModel (orchestrates ingestion, delta engine, and UI state)
 +-- MainWindow.xaml           712-line ATLAS-style dark-mode workbench shell

 OpenATLAS.ConsoleApp          Headless console harness for ingestion testing
 OpenATLAS.Tests               xUnit test suite (CircularBuffer, LiveDeltaEngine, ReferenceLapIndex, Workbench UI)
```

---

## Technology Stack

| Layer | Technology |
|---|---|
| Reference Pipeline | Python 3, FastF1, OpenF1 API, SciPy, Pandas, PyArrow |
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
- Python 3.9+ (for reference data extraction only)

### 1. Clone the Repository

```bash
git clone https://github.com/<your-username>/OpenATLAS.git
cd OpenATLAS
```

### 2. Extract Reference Data

Install Python dependencies and download reference laps:

```bash
pip install fastf1 scipy pandas pyarrow
```

```bash
# Export every driver from the 2024 Monza qualifying session (default source: OpenF1)
python extract_reference.py --year 2024 --track Monza

# Export a single driver
python extract_reference.py --year 2023 --track Silverstone --driver HAM

# Use FastF1 as the source instead of OpenF1
python extract_reference.py --year 2023 --track Monza --source fastf1

# Export a race session
python extract_reference.py --year 2024 --track Spa --session R
```

Reference laps are saved to `references/<Year>/<Track>/<DRIVER>.parquet`.

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

### Reference Lap Selection

Use the dropdown bar at the top of the Workbench window to select a **Year**, **Track**, and **Driver**. The trace panes update immediately.

### Live Mode (Assetto Corsa / ACC)

1. Launch Assetto Corsa or ACC and enter a session on a track that matches your loaded reference.
2. Click **Connect Live** in the Workbench toolbar.
3. The application reads telemetry from shared memory (`Local\acpmf_physics`) at 60 Hz and plots live traces against the reference baseline.

### Simulation Replay Mode
I built this just to test because I didn't feel like driving laps around Monza every time I wanted to test my code. This will be mostly be replaced in the end with say, a utility where you can simulate your own sim lap data but let's see.

For now, if the simulator is not running, click **Start Simulation** to run a built-in 60 Hz dry-run replay. This plays back the reference lap with slight speed variations to demonstrate the delta engine, trace rendering, and UI responsiveness.

---

## Project Structure

```
OpenATLAS/
|-- Core/                        Core telemetry engine library
|   |-- Collections/             CircularBuffer<T>
|   |-- Engine/                  LiveDeltaEngine
|   |-- Models/                  AcPhysicsPacket, TelemetrySnapshot, LiveDeltaSnapshot, ReferenceSample
|   |-- Network/                 SharedMemoryListener, UdpTelemetryListener
|   |-- Reference/               ReferenceLapIndex, ReferenceLibrary
|-- Workbench/                   WPF desktop application
|   |-- Plotting/                TracePanes (ScottPlot integration)
|   |-- Themes/                  TracksideTheme.xaml
|   |-- ViewModels/              WorkbenchViewModel, ViewModelBase, RelayCommand
|   |-- MainWindow.xaml          Main application window
|-- ConsoleApp/                  Headless console test harness
|-- Tests/                       xUnit test suite
|-- references/                  Extracted F1 reference laps (.parquet)
|-- extract_reference.py         Python reference data pipeline
|-- openf1_source.py             OpenF1 API data source
|-- OpenATLAS.sln                Visual Studio solution
```

---

## Current Progress

### Completed

| Phase | Task | Description |
|---|---|---|
| 1 | 1.1 | FastF1 / OpenF1 session downloader with CLI |
| 1 | 1.2 | Distance-domain resampling (SciPy `interp1d`, 1-meter intervals) |
| 1 | 1.3 | Parquet export with downcasted types |
| 2 | 2.1 | `AcPhysicsPacket` struct with `StructLayout(Sequential, Pack=4)` |
| 2 | 2.2 | `SharedMemoryListener` (background `MemoryMappedFile` reader) and `UdpTelemetryListener` |
| 2 | 2.3 | Zero-copy struct marshaling via `MemoryMappedViewAccessor.Read` |
| 3 | 3.1 | Thread-safe `CircularBuffer<T>` with `Span<T>` bulk copy |
| 3 | 3.2 | `ReferenceLapIndex` (Parquet-to-parallel-array loader, O(1) distance lookup, sub-meter interpolation) |
| 3 | 3.3 | `LiveDeltaEngine` (real-time delta: Dt = t_ref(s) - t_sim(s), lap crossover detection) |
| 4 | 4.1 | WPF dark-mode workbench shell with MVVM, trackside theme, and reference library dropdowns |
| 4 | 4.2 | ScottPlot integration: three linked trace panes (Speed, Pedals, Delta) with pre-allocated scatter buffers |

### Remaining

| Phase | Task | Description |
|---|---|---|
| 4 | 4.3 | Decoupled UI render loop (`DispatcherTimer` at 60 Hz pulling from the ring buffer, instead of rendering on every packet) |
| 5 | 5.1 | Head-up delta bar widget (large centered +/- seconds display with green/red coloring) |
| 5 | 5.2 | Corner analysis table (automated per-corner splits: apex speed, braking point, throttle pickup) |
| 5 | 5.3 | Session saver and exporter (save completed sim laps as `.parquet` / `.csv` to become future reference baselines) |

---

## Reference Data

The `references/` folder ships with pre-extracted laps from multiple circuits and seasons:

```
references/
|-- 2023/
|   |-- Monza/
|   |-- Silverstone/
|-- 2024/
|   |-- Barcelona/
|   |-- Monza/
|   |-- Silverstone/
|   |-- Spa-Francorchamps/
```

To add more, run `extract_reference.py` with the desired year, track, and session.

---

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
