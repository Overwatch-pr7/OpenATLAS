# OpenATLAS: Trackside Telemetry & Delta Analysis Workbench

## Project Overview (ELI5)
When a Formula 1 driver completes a lap, race engineers do not simply stare at the final lap time; they analyze the car's vitals meter by meter. They compare the live car against a "reference lap" to answer questions like: Did the driver brake 10 meters too early into Turn 1? Did they hesitate on the throttle coming out of the chicane?

**OpenATLAS** is a lightweight, custom desktop clone of professional motorsport telemetry software (such as McLaren ATLAS or MoTeC i2 Pro) built using C# and .NET.

The system operates with two data streams:
1. **Live Input (Assetto Corsa):** Streams real-time telemetry (speed, throttle, brake, RPM, gear, steering angle) from your PC racing simulator at 60-100 Hz.
2. **Historical Reference (FastF1/Kaggle):** Real-world telemetry from actual Formula 1 Grand Prix sessions (e.g., Charles Leclerc or Lewis Hamilton around Monza).

The application resamples both laps onto a common distance axis, aligns their track positions, and renders real-time trace comparisons, dynamic delta times ($\Delta t$), and throttle/brake application zones.

---

## Architecture & Data Sources

### Data Sources
*   **Assetto Corsa (Sim):** Live UDP / Memory (60-100 Hz Raw Bytes)
*   **FastF1 / Historical F1:** Python Extraction (Standardized Laps)

### Core Components
*   **.NET Telemetry Core:** Struct Marshaling & Ingestion Thread
*   **Memory Management:** Thread-Safe Circular Ring Buffer
*   **Processing:** Distance Interpolator & Live Delta Engine
*   **Desktop Workbench UI (WPF):** 
    *   Decoupled UI Render Loop (Fixed 60 FPS)
    *   Multi-Trace Line Charts (ScottPlot)
    *   Real-time Time Delta Bar ($\pm$ seconds vs. reference)

---

## The Dry Run Walkthrough (ELI5)

### The Scenario
You are driving a lap around Monza in Assetto Corsa. In OpenATLAS, you load Lewis Hamilton's 2023 Italian GP qualifying lap as your reference baseline.

| Track Distance | 0m (Start/Finish) | 600m (Approaching Prima Variante) |
| :--- | :--- | :--- |
| **Hamilton:** | `[Speed: 345 km/h]` | `[Brakes at 620m, 100% Brake]` |
| **You (Sim):** | `[Speed: 338 km/h]` | `[Brakes at 580m, 80% Brake]` |
| **Live Delta Bar:** | `[ 0.00s]` | `[ -0.185s]` |

### Step-by-Step Execution
1. **Assetto Corsa Transmits:** You approach Turn 1 at Monza. The game broadcasts a 256-byte telemetry struct over local port 9996 (or shared memory).
2. **The Ingestion Service Marshals:** The C# background worker grabs the raw bytes, casts them into an `AcPhysicsPacket` struct, and extracts:
    *   `SpeedKmh` $= 338.2f$
    *   `DistanceTraveled` $= 580.4m$
    *   `Brake` $= 0.82f$
3. **The Delta Calculation:** The engine checks Hamilton's reference lap at exact distance $580.4m$:
    *   Hamilton's speed was $345.1\text{ km/h}$; his brake pressure was $0.00$ (still coasting).
    *   You braked 40 meters earlier than Hamilton.
    *   The calculation engine updates the live delta: $\Delta t = -0.18$ seconds.
4. **The UI Renders (ScottPlot):** The UI render loop runs on a 60 FPS clock, pulling the latest snapshot:
    *   Trace Channel 1 draws your brake line spiking while Hamilton's is flat.
    *   The live Delta Bar turns red and displays `-0.18s`.

---

## Phase-Wise Implementation Plan

This roadmap is broken into progressive milestones designed for clear execution by a developer or an AI assistant.

*   **Phase 1:** Reference Pipeline (Python & FastF1)
*   **Phase 2:** Sim Ingestion Worker (C# Raw Bytes & Sockets)
*   **Phase 3:** Telemetry Normalization & Delta Engine (C#)
*   **Phase 4:** Desktop Workbench UI (WPF & ScottPlot)
*   **Phase 5:** Trackside Workbench Polish & Analysis Tools

---

### Phase 1: The Reference Data Pipeline (Python & FastF1)
**Goal:** Extract real-world telemetry from official F1 sessions, normalize it into distance metrics, and save it in a clean format for .NET.

*   **Task 1.1 - FastF1 Session Downloader:** Write a Python script (`extract_reference.py`) using `fastf1` to download a specific Grand Prix qualifying session (e.g., Monza 2023, Pole Lap).
*   **Task 1.2 - Distance-Domain Resampling:**
    *   FastF1 telemetry samples are recorded on time timestamps (~10-20 Hz).
    *   Interpolate the telemetry (Speed, Throttle, Brake, RPM, nGear) onto an equidistant track distance array $\langle s=0,1,2,...,L_{tr} \rangle$ meters using `scipy.interpolate.interp1d`.
*   **Task 1.3 - Output Standardization:** Save the resampled lap to a standardized JSON or CSV file containing fields: `distance_m`, `time_s`, `speed_kmh`, `throttle`, `brake`, `gear`.
*   **Deliverable:** A single file `monza_reference_lap.json` that provides the baseline for the entire C# engine.

---

### Phase 2: Ingestion Worker & Struct Marshaling (C#.NET)
**Goal:** Capture live telemetry packets from Assetto Corsa on a background thread without freezing the main application.

*   **Task 2.1 - Telemetry Struct Definition:**
    *   Assetto Corsa exposes telemetry via Windows Shared Memory (`acpmf_physics`) or UDP socket (port 9996).
    *   Define explicit C# layout structs using `[StructLayout(LayoutKind.Sequential, Pack = 4)]` to mirror the game's native C++ memory representation.
*   **Task 2.2 - Background Listener Worker:**
    *   Use `System.IO.MemoryMappedFiles` (for Shared Memory) or `UdpClient` running inside a dedicated `Task.Run()` background thread.
    *   Read incoming bytes into a pre-allocated byte buffer to avoid memory allocations in the loop.
*   **Task 2.3 - Pointer / Struct Marshaling:**
    *   Unpack raw bytes into your telemetry struct using `Marshal.PtrToStructure` or unmanaged pointer casting (fixed blocks in `unsafe` C#).
*   **Deliverable:** A C# console test that launches, connects to Assetto Corsa while you drive, and logs live speed, throttle, and lap distance to the console at 60+ Hz with zero latency.

---

### Phase 3: Telemetry Normalization & Delta Engine (C# Core Logic)
**Goal:** Maintain live telemetry history in memory and calculate the real-time time delta ($\Delta t$) against the reference lap.

*   **Task 3.1 - Thread-Safe Ring Buffer (Circular Buffer):**
    *   Implement a generic, fixed-capacity circular buffer `CircularBuffer<TelemetrySnapshot>` (e.g., capacity: 10,000 samples).
    *   As new simulator packets arrive, overwrite the oldest sample. This avoids unbounded memory growth and keeps GC pressure at zero.
*   **Task 3.2 - Reference Lap In-Memory Index:**
    *   Parse `monza_reference_lap.json` into a lookup array indexed by distance ($1\text{ index} = 1\text{ meter}$).
*   **Task 3.3 - The Delta Algorithm:**
    *   For the car's current distance on track ($s_{sim}$), calculate elapsed time: $t_{sim}(s)$.
    *   Fetch the reference time at that exact distance: $t_{ref}(s)$
    *   Compute: $\Delta t = t_{ref}(s) - t_{sim}(s)$.
    *   A positive value indicates you are ahead of the reference; a negative value indicates you are behind.
*   **Deliverable:** A background calculation engine that continuously emits normalized data packets containing: `(Distance, SimSpeed, RefSpeed, SimBrake, RefBrake, DeltaTime)`.

---

### Phase 4: Desktop Workbench UI (C# WPF & ScottPlot)
**Goal:** Build an ATLAS-inspired dark-mode desktop interface that renders high-frequency traces smoothly.

*   **Task 4.1 - UI Shell Setup:**
    *   Create a modern WPF desktop project (.NET 8 or 9).
    *   Style the window with trackside telemetry aesthetics: dark gray/matte black background (`#1E1E1E`), high-contrast colored lines (Cyan for live, Yellow/White for reference).
*   **Task 4.2 - Charting Integration (ScottPlot):**
    *   Install `ScottPlot.WPF` via NuGet.
    *   Set up three synchronized plot panes sharing an identical horizontal X-axis (Distance in meters):
        1.  **Speed Trace:** Live Speed vs. Reference Speed.
        2.  **Pedal Trace:** Throttle (0-100%) and Brake (0-100%).
        3.  **Delta Time Plot:** Continuous $\Delta t$ line showing where time is gained or lost.
*   **Task 4.3 - Decoupled UI Render Loop:**
    *   *Critical:* Do not update the chart whenever a UDP/memory packet arrives.
    *   Use a `DispatcherTimer` running strictly at 60 Hz.
    *   On every UI tick, copy the latest segment from the ring buffer, refresh the ScottPlot scatter buffers, and call `WpfPlot.Refresh()`.
*   **Deliverable:** A functioning desktop client that plots real-time traces against Hamilton's lap while you drive without dropping frames or freezing the interface.

---

### Phase 5: Analysis Features & Resume Polish
**Goal:** Turn the live monitor into an authentic diagnostic workbench.

*   **Task 5.1 - The Head-Up Delta Bar:**
    *   Build a central vertical/horizontal bar widget displaying the current delta time (e.g., `+0.42s` in bright green if faster, `-0.28s` in bright red if slower).
*   **Task 5.2 - Corner Analysis Table:**
    *   Parse track corner markers (e.g., Turn 1: 600m-1100m).
    *   Generate an automated split table comparing: Minimum Apex Speed, Braking Point Distance, and Throttle Pick-Up Distance.
*   **Task 5.3 - Session Saver & Exporter:**
    *   Allow completed simulator laps to be saved as `.parquet` or `.csv` files so your own laps can become the new reference for future sessions.
*   **Deliverable:** A polished, fully documented GitHub repository featuring architecture diagrams, real-time performance benchmarks (CPU/memory allocations), and a video demonstrating the sim lap overlaying an actual F1 driver's trace.