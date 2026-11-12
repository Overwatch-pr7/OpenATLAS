using Xunit;
using OpenATLAS.Core.Engine;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Tests.Engine
{
    /// <summary>
    /// Verifies live lap distance and time from AC's own lap position, lap counter and lap clock.
    /// </summary>
    public class AcLapTrackerTests
    {
        private const float Length = 5761f;

        private static AcLapPosition Live(AcLapTracker t, int laps, float position, int timeMs, bool inPit = false) =>
            t.Update(AcStatus.Live, laps, position, timeMs, inPit);

        [Fact]
        public void Distance_IsLapFractionTimesReferenceLength_AndTimeIsAcsLapClock()
        {
            var t = new AcLapTracker(Length);
            Live(t, 1, 0.001f, 100);

            AcLapPosition p = Live(t, 1, 0.5f, 41_250);

            Assert.True(p.IsTimed);
            Assert.Equal(0.5f * Length, p.DistanceMeters, 1);
            Assert.Equal(41.25f, p.LapTimeSeconds, 3);
        }

        [Fact]
        public void OutLap_IsNotCompared_UntilTheLine()
        {
            var t = new AcLapTracker(Length);

            Assert.False(Live(t, 0, 0.93f, 2_000).IsTimed);   // leaving the pits partway round
            Assert.Equal(AcLapState.WaitingForLine, t.State);
            Assert.False(Live(t, 0, 0.99f, 20_000).IsTimed);

            AcLapPosition p = Live(t, 1, 0.0005f, 16);       // crossed the line
            Assert.True(p.IsTimed);
            Assert.True(p.NewLap);
            Assert.Equal(AcLapState.Timing, t.State);
        }

        [Fact]
        public void AroundTheLine_PositionAndLapCounterOutOfStep_StayAtTheEnds()
        {
            var t = new AcLapTracker(Length);
            Live(t, 1, 0f, 0);

            // Position already wrapped, lap counter not yet: still the end of this lap
            Assert.Equal(Length, Live(t, 1, 0.002f, 82_000).DistanceMeters, 1);

            // Lap counter changed, position still reads the end: start of the new lap
            Assert.Equal(0f, Live(t, 2, 0.999f, 10).DistanceMeters, 1);
        }

        [Fact]
        public void Pause_StopsSamples_AndResumesTheSameLap()
        {
            var t = new AcLapTracker(Length);
            Live(t, 1, 0f, 0);
            Live(t, 1, 0.3f, 25_000);

            Assert.False(t.Update(AcStatus.Pause, 1, 0.3f, 25_000, false).IsTimed);
            Assert.Equal(AcLapState.Paused, t.State);

            AcLapPosition p = Live(t, 1, 0.301f, 25_100);
            Assert.True(p.IsTimed);
            Assert.False(p.NewLap);
        }

        [Fact]
        public void ReturningToThePits_StopsTheComparison_UntilTheNextLapFromTheLine()
        {
            var t = new AcLapTracker(Length);
            Live(t, 1, 0f, 0);
            Live(t, 1, 0.6f, 50_000);

            Assert.False(Live(t, 1, 0.92f, 50_100, inPit: true).IsTimed);
            Assert.False(Live(t, 1, 0.95f, 60_000).IsTimed);   // driving the out lap
            Assert.True(Live(t, 2, 0.001f, 20).IsTimed);
        }

        [Fact]
        public void TeleportBackwards_StopsTheComparison()
        {
            var t = new AcLapTracker(Length);
            Live(t, 1, 0f, 0);
            Live(t, 1, 0.6f, 50_000);

            Assert.False(Live(t, 1, 0.2f, 50_100).IsTimed);
            Assert.Equal(AcLapState.WaitingForLine, t.State);
        }
    }
}
