using System;
using System.Linq;
using Xunit;
using OpenATLAS.Core.Analysis;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Reference;

namespace OpenATLAS.Tests.Analysis
{
    /// <summary>
    /// Verifies the per-corner measurements behind the corner analysis table (Task 5.2).
    /// </summary>
    public class CornerAnalyzerTests
    {
        private static TrackCorner Corner(int number, float distance) => new(number, "", distance, "");

        /// <summary>
        /// A 2,000 m lap with one corner at 1,000 m: full throttle at 300 km/h, braking from
        /// <paramref name="brakeAt"/>, slowest (<paramref name="apexSpeed"/>) at 1,000 m and back on the
        /// throttle at <paramref name="throttleAt"/>. Every metre is sampled, scaled by <paramref name="scale"/>.
        /// </summary>
        public static LapTrace OneCornerLap(double brakeAt = 850, double apexSpeed = 100, double throttleAt = 1020,
                                            double scale = 1.0, double coveredMeters = 2000)
        {
            int n = (int)(coveredMeters * scale) + 1;
            var d = new double[n];
            var s = new double[n];
            var t = new double[n];
            var b = new double[n];
            var time = new double[n];

            for (int i = 0; i < n; i++)
            {
                double x = i / scale; // position on the 2,000 m lap
                d[i] = i;
                if (x < brakeAt)
                {
                    s[i] = 300; t[i] = 100;
                }
                else if (x <= 1000)
                {
                    s[i] = 300 - (300 - apexSpeed) * (x - brakeAt) / (1000 - brakeAt);
                    b[i] = x < 980 ? 100 : 0;
                }
                else
                {
                    // Holds the apex speed until back on the throttle, then accelerates
                    s[i] = x < throttleAt ? apexSpeed : Math.Min(300, apexSpeed + (x - throttleAt) * 0.5);
                    t[i] = x >= throttleAt ? 100 : 20;
                }
                time[i] = i == 0 ? 0 : time[i - 1] + 1.0 / (Math.Max(s[i], 1) / 3.6);
            }

            return new LapTrace(d, s, t, b, time, 2000 * scale);
        }

        private static CornerZone MiddleZone() =>
            CornerAnalyzer.BuildZones([Corner(1, 1000)], 2000)[0];

        // ---------------------------------------------------------------- Zones

        [Fact]
        public void BuildZones_SplitHalfwayBetweenCorners_AndCapLongStraights()
        {
            var zones = CornerAnalyzer.BuildZones([Corner(2, 953), Corner(1, 895), Corner(11, 5200)], 5763);

            Assert.Equal(["T1", "T2", "T11"], zones.Select(z => z.Label));
            Assert.Equal(895 - CornerAnalyzer.MaxApproachMeters, zones[0].StartMeters, 1);
            Assert.Equal(924, zones[0].EndMeters, 1);                 // halfway to T2
            Assert.Equal(924, zones[1].StartMeters, 1);
            Assert.Equal(953 + CornerAnalyzer.MaxExitMeters, zones[1].EndMeters, 1);
            Assert.Equal(5200 - CornerAnalyzer.MaxApproachMeters, zones[2].StartMeters, 1);
            Assert.Equal(5600, zones[2].EndMeters, 1);
        }

        // ---------------------------------------------------------------- Measurements

        [Fact]
        public void Measure_FindsApexSpeed_BrakingPoint_AndThrottlePickUp()
        {
            var m = CornerAnalyzer.Measure(OneCornerLap(), MiddleZone());

            Assert.Equal(100, m.MinSpeedKmh!.Value, 1);
            Assert.Equal(1000, m.MinSpeedAtMeters!.Value, 1);
            Assert.Equal(850, m.BrakePointMeters);
            Assert.Equal(1020, m.ThrottlePickUpMeters);
            Assert.False(m.IsFlatOut);
            Assert.NotNull(m.ZoneSeconds);
        }

        [Fact]
        public void Measure_FlatOutCorner_HasNoBrakeAndReportsFlat()
        {
            var lap = OneCornerLap(brakeAt: 2001, coveredMeters: 2000); // never brakes or lifts
            var m = CornerAnalyzer.Measure(lap, MiddleZone());

            Assert.Null(m.BrakePointMeters);
            Assert.Null(m.ThrottlePickUpMeters);
            Assert.True(m.IsFlatOut);
        }

        [Fact]
        public void Measure_LapNotYetAtTheCorner_IsNotReached()
        {
            var m = CornerAnalyzer.Measure(OneCornerLap(coveredMeters: 700), MiddleZone());

            Assert.False(m.HasData);
        }

        [Fact]
        public void Measure_PastTheApexButNotTheZoneEnd_HasNoTimeYet()
        {
            var m = CornerAnalyzer.Measure(OneCornerLap(coveredMeters: 1100), MiddleZone());

            Assert.True(m.HasData);
            Assert.Null(m.ZoneSeconds);
        }

        [Fact]
        public void Measure_LongerLap_IsScaledBackOntoTheReferenceMetres()
        {
            // Same driving on a track 1 % longer (an AC track vs the F1 lap)
            var m = CornerAnalyzer.Measure(OneCornerLap(scale: 1.01), MiddleZone(), scale: 1.01);

            Assert.InRange(m.BrakePointMeters!.Value, 849, 851);
            Assert.InRange(m.ThrottlePickUpMeters!.Value, 1019, 1021);
        }

        [Fact]
        public void Measure_RealMonzaLap_BrakesBeforeTheFirstChicane()
        {
            var lap = LapTrace.FromLap(ReferenceLapIndex.FromParquetFile(TempReferenceLibrary.SourceLapPath));
            var zones = CornerAnalyzer.BuildZones([Corner(1, 895), Corner(2, 953)], lap.LengthMeters);

            var t1 = CornerAnalyzer.Measure(lap, zones[0]);

            // An F1 car brakes on the run to the Prima Variante from well over 300 km/h
            Assert.InRange(t1.BrakePointMeters!.Value, 650, 895);
            Assert.InRange(t1.MinSpeedKmh!.Value, 60, 160);
        }
    }
}
