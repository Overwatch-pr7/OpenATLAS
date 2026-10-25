using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Engine;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Network;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.ConsoleApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "OpenATLAS - Phase 3 Live Telemetry & Delta Workbench Monitor";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("===============================================================================");
            Console.WriteLine("       OpenATLAS: Trackside Telemetry & Delta Analysis Workbench (Phase 3)     ");
            Console.WriteLine("===============================================================================");
            Console.ResetColor();

            // 1. Task 3.2: Load Reference Lap Index from Parquet
            string parquetPath = FindReferenceParquetFile();
            Console.WriteLine($"\n[1/3] Loading Reference Lap from: {Path.GetFileName(parquetPath)}...");

            ReferenceLapIndex referenceLap;
            try
            {
                referenceLap = await ReferenceLapIndex.FromParquetFileAsync(parquetPath);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"      -> Reference Lap Loaded Successfully!");
                Console.WriteLine($"      -> Track Length: {referenceLap.TrackLengthMeters} meters | Samples: {referenceLap.SampleCount}");
                Console.WriteLine($"      -> Baseline Lap Time: {TimeSpan.FromSeconds(referenceLap.TotalLapTimeSeconds):mm\\:ss\\.fff} ({referenceLap.TotalLapTimeSeconds:F3}s)");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Failed to load Parquet reference lap: {ex.Message}");
                Console.ResetColor();
                return;
            }

            // 2. Task 3.1: Initialize Thread-Safe Circular Ring Buffer
            const int bufferCapacity = 1000;
            var circularBuffer = new CircularBuffer<LiveDeltaSnapshot>(bufferCapacity);
            Console.WriteLine($"[2/3] Initialized CircularBuffer<LiveDeltaSnapshot> (Capacity: {bufferCapacity}, 0 GC Allocations)");

            // 3. Task 3.3: Initialize The Delta Engine
            var deltaEngine = new LiveDeltaEngine(referenceLap, circularBuffer);
            deltaEngine.OnLapCompleted += () =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n>>> [LAP COMPLETED] Start/Finish line crossed! Lap distance reset. <<<\n");
                Console.ResetColor();
            };
            Console.WriteLine("[3/3] Initialized LiveDeltaEngine (Hamilton 2023 Monza Q baseline)\n");

            // Select Execution Mode
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine("Select Test Mode:");
            Console.WriteLine("  [1] Live Assetto Corsa Competizione (Connect to ACC Shared Memory)");
            Console.WriteLine("  [2] Simulated Monza Lap Dry-Run (Verifies Phase 3 pipeline without game running)");
            Console.Write("Enter option (1 or 2, default is 1): ");
            
            string? choice = Console.ReadLine()?.Trim();
            if (choice == "2")
            {
                await RunSimulatedDryRunAsync(referenceLap, deltaEngine, circularBuffer);
            }
            else
            {
                RunLiveSharedMemoryMonitor(deltaEngine, circularBuffer, referenceLap);
            }
        }

        // Mode 1: Live ACC Shared Memory Ingestion
        private static void RunLiveSharedMemoryMonitor(
            LiveDeltaEngine deltaEngine,
            CircularBuffer<LiveDeltaSnapshot> circularBuffer,
            ReferenceLapIndex referenceLap)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[WAITING] Listening for Assetto Corsa Competizione (`Local\\acpmf_physics`)...");
            Console.WriteLine("Start driving in ACC. Live telemetry and real-time Delta will display below.\n");
            Console.ResetColor();

            using var listener = new SharedMemoryListener();

            float accumulatedDistance = 0f;
            float accumulatedLapTime = 0f;
            DateTime lastPacketTime = DateTime.UtcNow;
            int frameCount = 0;

            listener.OnTelemetryUpdated += (packet) =>
            {
                DateTime now = DateTime.UtcNow;
                float dt = (float)(now - lastPacketTime).TotalSeconds;
                lastPacketTime = now;

                if (dt <= 0f || dt > 0.5f) dt = 0.016f; // Clamp anomalous frame deltas

                // Integrate distance traveled: ds = v (m/s) * dt (s)
                float speedMps = packet.SpeedKmh / 3.6f;
                accumulatedDistance += speedMps * dt;
                accumulatedLapTime += dt;

                // Handle lap wrap-around if car crosses track length
                if (accumulatedDistance >= referenceLap.TrackLengthMeters)
                {
                    accumulatedDistance = 0f;
                    accumulatedLapTime = 0f;
                    deltaEngine.Reset();
                }

                // Feed into Phase 3 Delta Engine
                LiveDeltaSnapshot delta = deltaEngine.Process(packet, accumulatedDistance, accumulatedLapTime);

                frameCount++;
                // Print at ~4 Hz (every 15 frames at 60Hz) so console stays readable
                if (frameCount % 15 == 0)
                {
                    PrintTelemetryRow(delta, circularBuffer.Count);
                }
            };

            listener.Start();

            Console.WriteLine("Press [ENTER] to stop monitoring and return.\n");
            Console.ReadLine();
            listener.Stop();
        }

        // Mode 2: Simulated Dry Run for instant Phase 3 verification
        private static async Task RunSimulatedDryRunAsync(
            ReferenceLapIndex referenceLap,
            LiveDeltaEngine deltaEngine,
            CircularBuffer<LiveDeltaSnapshot> circularBuffer)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[SIMULATION] Starting 60Hz simulated Monza lap replay...");
            Console.WriteLine("Simulating driver telemetry vs Lewis Hamilton reference baseline...\n");
            Console.ResetColor();

            // Simulate car traveling from 0m to track length at ~60 Hz
            float simDistance = 0f;
            float simTime = 0f;
            int frameCount = 0;

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            while (!cts.IsCancellationRequested && simDistance <= referenceLap.TrackLengthMeters)
            {
                // Fetch reference at this distance and inject slight delta variation
                ReferenceSample refSample = referenceLap.GetSampleAtDistance(simDistance);

                // Driver is slightly slower (+0.2s) in sector 1, slightly faster (-0.1s) in sector 2
                float simSpeed = refSample.SpeedKmh * (simDistance < 2000f ? 0.985f : 1.01f);
                float speedMps = simSpeed / 3.6f;

                float dt = 0.016f; // 60 Hz tick
                simDistance += speedMps * dt;
                simTime += dt;

                // Compute delta via Task 3.3 Delta Engine
                var delta = deltaEngine.Calculate(
                    simDistance: simDistance,
                    simTime: simTime,
                    simSpeed: simSpeed,
                    simThrottle: refSample.Throttle,
                    simBrake: refSample.Brake,
                    simGear: refSample.Gear,
                    simRpm: refSample.Rpms
                );

                // Push into Task 3.1 Circular Buffer
                circularBuffer.Push(delta);

                frameCount++;
                if (frameCount % 15 == 0)
                {
                    PrintTelemetryRow(delta, circularBuffer.Count);
                }

                await Task.Delay(16); // 60 Hz pacing
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[COMPLETE] Simulated lap finished. Ring buffer holds {circularBuffer.Count} live snapshots.");
            Console.ResetColor();
        }

        // Formats and prints an ATLAS-style live telemetry row
        private static void PrintTelemetryRow(LiveDeltaSnapshot delta, int ringBufferCount)
        {
            string timeStr = TimeSpan.FromSeconds(delta.SimTime).ToString("mm\\:ss\\.ff");
            string deltaSign = delta.DeltaTime >= 0 ? "+" : "-";
            string deltaVal = $"{deltaSign}{Math.Abs(delta.DeltaTime):F3}s";
            string statusStr = delta.IsAhead ? "FASTER" : "SLOWER";

            Console.Write($"[{timeStr}] Dist: {delta.Distance,5:F0}m | Speed: {delta.SimSpeed,5:F1} (Ref: {delta.RefSpeed,5:F1}) km/h | ");
            Console.Write($"Thr: {delta.SimThrottle,3:F0}% | Brk: {delta.SimBrake,3:F0}% | Delta: ");

            if (delta.DeltaTime >= 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
            }

            Console.Write($"{deltaVal,8} ({statusStr,6})");
            Console.ResetColor();

            Console.WriteLine($" | Buf: {ringBufferCount}/1000");
        }

        private static string FindReferenceParquetFile()
        {
            const string defaultLap = "references/2023/Monza/HAM.parquet";
            string[] candidatePaths = [
                defaultLap,
                "../" + defaultLap,
                "../../" + defaultLap,
                Path.Combine(AppContext.BaseDirectory, defaultLap),
                Path.Combine(AppContext.BaseDirectory, "../../../../" + defaultLap)
            ];

            foreach (var path in candidatePaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath)) return fullPath;
            }

            return defaultLap;
        }
    }
}
