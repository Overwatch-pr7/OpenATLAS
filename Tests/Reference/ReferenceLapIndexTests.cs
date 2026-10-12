using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Tests.Reference
{
    // Task 3.2: Unit Testing for Reference Lap In-Memory Index
    // Validates Parquet columnar reading into parallel arrays, O(1) distance lookups, and sub-meter interpolation.
    public class ReferenceLapIndexTests
    {
        private static string GetMonzaParquetPath()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../monza_reference_lap.parquet"));
        }

        [Fact]
        public void Constructor_MismatchedLengths_ThrowsArgumentException()
        {
            int[] distance = new int[5];
            float[] speed = new float[4]; // mismatched length
            sbyte[] throttle = new sbyte[5];
            sbyte[] brake = new sbyte[5];
            int[] rpm = new int[5];
            sbyte[] gear = new sbyte[5];
            float[] time = new float[5];

            Assert.Throws<ArgumentException>(() => new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time));
        }

        [Fact]
        public void Constructor_NullArrays_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new ReferenceLapIndex(null!, null!, null!, null!, null!, null!, null!));
        }

        [Fact]
        public async Task FromParquetFileAsync_LoadsMonzaReferenceSuccessfully()
        {
            string path = GetMonzaParquetPath();
            Assert.True(File.Exists(path), $"Parquet file not found at: {path}");

            var index = await ReferenceLapIndex.FromParquetFileAsync(path);

            Assert.NotNull(index);
            Assert.Equal(5764, index.SampleCount);
            Assert.Equal(5763, index.TrackLengthMeters);
            Assert.True(index.TotalLapTimeSeconds > 70f && index.TotalLapTimeSeconds < 90f);

            // Parallel array integrity checks
            Assert.Equal(0, index.Distance[0]);
            Assert.Equal(600, index.Distance[600]);
            Assert.Equal(5763, index.Distance[5763]);

            Assert.True(index.Speed[600] > 300f, "Speed approaching Prima Variante should exceed 300 km/h");
            Assert.Equal(8, index.Gear[600]);
            Assert.Equal(100, index.Throttle[600]);
            Assert.Equal(0, index.Brake[600]);
        }

        [Fact]
        public void DistanceLookup_SubMeterInterpolation_ReturnsAccurateValues()
        {
            int[] distance = [0, 1, 2];
            float[] speed = [200.0f, 210.0f, 220.0f];
            sbyte[] throttle = [100, 100, 100];
            sbyte[] brake = [0, 0, 50];
            int[] rpm = [10000, 10500, 11000];
            sbyte[] gear = [5, 5, 5];
            float[] time = [0.0f, 0.05f, 0.10f];

            var index = new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);

            // Exact integer lookups
            Assert.Equal(200.0f, index.GetSpeedAtDistance(0f));
            Assert.Equal(210.0f, index.GetSpeedAtDistance(1f));

            // Halfway sub-meter interpolation (0.5m)
            float speedAtHalf = index.GetSpeedAtDistance(0.5f);
            Assert.Equal(205.0f, speedAtHalf);

            float timeAtHalf = index.GetTimeAtDistance(0.5f);
            Assert.Equal(0.025f, timeAtHalf);

            int rpmAtHalf = index.GetRpmAtDistance(0.5f);
            Assert.Equal(10250, rpmAtHalf);

            // Snapshot retrieval with zero heap allocation
            ReferenceSample sample = index.GetSampleAtDistance(0.5f);
            Assert.Equal(0.5f, sample.DistanceMeters);
            Assert.Equal(205.0f, sample.SpeedKmh);
            Assert.Equal(0.025f, sample.TimeSeconds);
            Assert.Equal(10250, sample.Rpms);
        }

        [Fact]
        public void DistanceLookup_BoundaryConditions_ClampsCorrectly()
        {
            int[] distance = [0, 1, 2];
            float[] speed = [100.0f, 150.0f, 200.0f];
            sbyte[] throttle = [100, 100, 100];
            sbyte[] brake = [0, 0, 0];
            int[] rpm = [5000, 6000, 7000];
            sbyte[] gear = [2, 3, 4];
            float[] time = [0.0f, 0.1f, 0.2f];

            var index = new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);

            // Negative distance should clamp to index 0
            Assert.Equal(100.0f, index.GetSpeedAtDistance(-10.0f));
            Assert.Equal(0.0f, index.GetTimeAtDistance(-5.0f));

            // Beyond track length should clamp to final sample
            Assert.Equal(200.0f, index.GetSpeedAtDistance(100.0f));
            Assert.Equal(0.2f, index.GetTimeAtDistance(50.0f));
        }

        [Fact]
        public async Task DistanceLookup_HighThroughput_ZeroAllocationInHotLoop()
        {
            string path = GetMonzaParquetPath();
            var index = await ReferenceLapIndex.FromParquetFileAsync(path);

            // Warm up
            for (int i = 0; i < 100; i++)
            {
                _ = index.GetSampleAtDistance(i * 1.5f);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            // 100,000 lookups simulating delta engine and UI ticks
            for (int i = 0; i < 100000; i++)
            {
                float testDist = (i % 5760) + 0.35f;
                ReferenceSample sample = index.GetSampleAtDistance(testDist);
                _ = sample.SpeedKmh;
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Direct struct return from parallel arrays achieves strictly 0 GC allocations
            Assert.Equal(0, totalAllocated);
        }
    }
}
