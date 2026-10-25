using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Engine;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Tests.Engine
{
    // Task 3.3: Unit Testing for The Delta Algorithm & Live Calculation Engine
    // Validates real-time delta calculation (t_ref - t_sim), sign conventions,
    // event dispatch, ring buffer integration, and 0 GC allocations.
    public class LiveDeltaEngineTests
    {
        private static ReferenceLapIndex CreateMockReferenceLap()
        {
            // Equidistant 1-meter reference from 0m to 200m
            // 0m = 0.0s, 100m = 2.0s, 200m = 4.0s (0.02s per meter)
            int count = 201;
            int[] distance = new int[count];
            float[] speed = new float[count];
            sbyte[] throttle = new sbyte[count];
            sbyte[] brake = new sbyte[count];
            int[] rpm = new int[count];
            sbyte[] gear = new sbyte[count];
            float[] time = new float[count];

            for (int i = 0; i < count; i++)
            {
                distance[i] = i;
                speed[i] = 100f + i; // 100 km/h to 300 km/h
                throttle[i] = 100;
                brake[i] = 0;
                rpm[i] = 5000 + (i * 35);
                gear[i] = 4;
                time[i] = i * 0.02f; // 100m = 2.0s
            }

            return new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);
        }

        private static string GetMonzaParquetPath()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../references/2023/Monza/HAM.parquet"));
        }

        [Fact]
        public void DeltaCalculation_WhenSimIsFaster_ReturnsPositiveDelta()
        {
            var refLap = CreateMockReferenceLap();
            var engine = new LiveDeltaEngine(refLap);

            // At 100m, reference time is 2.00s.
            // Sim car arrives at 100m in 1.85s (faster by 0.15s).
            LiveDeltaSnapshot result = engine.Calculate(
                simDistance: 100f,
                simTime: 1.85f,
                simSpeed: 210f,
                simThrottle: 1.0f,
                simBrake: 0.0f,
                simGear: 4,
                simRpm: 9200
            );

            // Delta = 2.00 - 1.85 = +0.15s
            Assert.Equal(0.15f, result.DeltaTime, precision: 3);
            Assert.True(result.IsAhead, "Sim should be marked as ahead when delta is positive.");
            Assert.Equal(100f, result.RefThrottle);
            Assert.Equal(100f, result.SimThrottle); // 1.0f normalized to 100%
            Assert.Equal(200f, result.RefSpeed);
            Assert.Equal(210f, result.SimSpeed);
        }

        [Fact]
        public void DeltaCalculation_WhenSimIsSlower_ReturnsNegativeDelta()
        {
            var refLap = CreateMockReferenceLap();
            var engine = new LiveDeltaEngine(refLap);

            // At 100m, reference time is 2.00s.
            // Sim car arrives at 100m in 2.25s (slower by 0.25s).
            LiveDeltaSnapshot result = engine.Calculate(
                simDistance: 100f,
                simTime: 2.25f,
                simSpeed: 190f,
                simThrottle: 100f,
                simBrake: 0f,
                simGear: 4,
                simRpm: 8800
            );

            // Delta = 2.00 - 2.25 = -0.25s
            Assert.Equal(-0.25f, result.DeltaTime, precision: 3);
            Assert.False(result.IsAhead, "Sim should be marked as behind when delta is negative.");
        }

        [Fact]
        public void Process_TelemetrySnapshot_FiresEvent_AndPushesToOutputBuffer()
        {
            var refLap = CreateMockReferenceLap();
            var ringBuffer = new CircularBuffer<LiveDeltaSnapshot>(100);
            var engine = new LiveDeltaEngine(refLap, ringBuffer);

            LiveDeltaSnapshot? receivedFromEvent = null;
            engine.OnDeltaCalculated += (snap) => receivedFromEvent = snap;

            var snapshot = new TelemetrySnapshot(
                timestampSeconds: 1.90,
                distanceMeters: 100f,
                speedKmh: 205f,
                throttle: 1.0f,
                brake: 0.0f,
                gear: 4,
                rpms: 9100
            );

            LiveDeltaSnapshot result = engine.Process(snapshot);

            // Event fired
            Assert.NotNull(receivedFromEvent);
            Assert.Equal(result.DeltaTime, receivedFromEvent.Value.DeltaTime);

            // Ring buffer populated
            Assert.Equal(1, ringBuffer.Count);
            Assert.Equal(result.DeltaTime, ringBuffer[0].DeltaTime);
        }

        [Fact]
        public void Process_AcPhysicsPacket_ConvertsAndProcessesAccurately()
        {
            var refLap = CreateMockReferenceLap();
            var engine = new LiveDeltaEngine(refLap);

            var packet = new AcPhysicsPacket
            {
                SpeedKmh = 205f,
                Gas = 0.95f,
                Brake = 0.05f,
                Gear = 4,
                Rpms = 9100
            };

            LiveDeltaSnapshot result = engine.Process(packet, simDistance: 100f, simElapsedSeconds: 1.95f);

            Assert.Equal(205f, result.SimSpeed);
            Assert.Equal(95f, result.SimThrottle, precision: 1); // 0.95 converted to 95%
            Assert.Equal(5f, result.SimBrake, precision: 1);    // 0.05 converted to 5%
            Assert.Equal(0.05f, result.DeltaTime, precision: 3);
        }

        [Fact]
        public void LapCrossover_TriggersOnLapCompletedEvent()
        {
            var refLap = CreateMockReferenceLap();
            var engine = new LiveDeltaEngine(refLap);

            bool lapCompletedFired = false;
            engine.OnLapCompleted += () => lapCompletedFired = true;

            var endOfLap = new TelemetrySnapshot(timestampSeconds: 80.0, distanceMeters: 195f, speedKmh: 250f, throttle: 1f, brake: 0f, gear: 5, rpms: 10000);
            var startOfNextLap = new TelemetrySnapshot(timestampSeconds: 80.1, distanceMeters: 5f, speedKmh: 255f, throttle: 1f, brake: 0f, gear: 5, rpms: 10200);

            engine.Process(endOfLap);
            Assert.False(lapCompletedFired);

            engine.Process(startOfNextLap);
            Assert.True(lapCompletedFired, "Lap crossover should be detected when distance drops near 0.");
        }

        [Fact]
        public async Task MonzaReference_RealWorldScenarioTest()
        {
            string path = GetMonzaParquetPath();
            var refLap = await ReferenceLapIndex.FromParquetFileAsync(path);
            var engine = new LiveDeltaEngine(refLap);

            // Scenario from openatlas_idea.md Walkthrough:
            // At 580m approaching Prima Variante:
            // Hamilton ref speed ~340 km/h, brake = 0
            // Sim brakes at 580m with 80% brake, sim speed 338 km/h
            ReferenceSample refAt580 = refLap.GetSampleAtDistance(580f);
            Assert.True(refAt580.SpeedKmh > 330f);
            Assert.Equal(0, refAt580.Brake);

            float simTime = refAt580.TimeSeconds + 0.185f; // Sim is 0.185s slower
            LiveDeltaSnapshot delta = engine.Calculate(
                simDistance: 580f,
                simTime: simTime,
                simSpeed: 338f,
                simThrottle: 0f,
                simBrake: 0.80f,
                simGear: 7,
                simRpm: 11000
            );

            // Delta = refTime - simTime = -0.185s
            Assert.Equal(-0.185f, delta.DeltaTime, precision: 3);
            Assert.False(delta.IsAhead);
            Assert.Equal(80f, delta.SimBrake, precision: 1);
            Assert.Equal(0f, delta.RefBrake);
        }

        [Fact]
        public void ZeroAllocations_UnderHighFrequencyProcessingLoop()
        {
            var refLap = CreateMockReferenceLap();
            var ringBuffer = new CircularBuffer<LiveDeltaSnapshot>(1000);
            var engine = new LiveDeltaEngine(refLap, ringBuffer);

            var sampleSnapshot = new TelemetrySnapshot(
                timestampSeconds: 1.5,
                distanceMeters: 75f,
                speedKmh: 175f,
                throttle: 1f,
                brake: 0f,
                gear: 3,
                rpms: 7500
            );

            // Warm up
            for (int i = 0; i < 50; i++)
            {
                engine.Process(sampleSnapshot);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            // 100,000 live updates processed into ring buffer
            for (int i = 0; i < 100000; i++)
            {
                engine.Process(sampleSnapshot);
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Engine calculation and ring buffer insertion must allocate 0 bytes on the GC heap
            Assert.Equal(0, totalAllocated);
        }
    }
}
