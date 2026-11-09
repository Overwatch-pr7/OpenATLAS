using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.Plotting;
using OpenATLAS.Workbench.ViewModels;
using ScottPlot;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the distance cursor: live value lookup, hover/pin behaviour, and the header readouts.
    /// </summary>
    public class DistanceCursorTests
    {
        // Live sample with distinct values so interpolation is easy to check
        private static LiveDeltaSnapshot Sample(float distance, float speed, float throttle, float delta) =>
            new(distance, speed, 0f, throttle, 0f, 0f, 0f, 6, 6, 10000, 10000, 1f, 1f, delta);

        private static readonly LiveDeltaSnapshot[] Lap =
        [
            Sample(100f, 200f, 100f, 0.00f),
            Sample(200f, 220f, 50f, -0.10f),
            Sample(300f, 240f, 0f, -0.20f),
        ];

        private static TracePanes CreatePanes() => new(new Plot(), new Plot(), new Plot(), 100);

        // Monza fixture lap via the reference library, so the view model has a reference loaded
        private static (TempReferenceLibrary temp, WorkbenchViewModel vm, TracePanes panes, DistanceCursor cursor) Create()
        {
            var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            var vm = new WorkbenchViewModel(library: temp.Library);
            var panes = CreatePanes();
            panes.LoadReference(vm.ReferenceLap!);
            return (temp, vm, panes, new DistanceCursor(panes, vm));
        }

        [Fact]
        public void TryGetLiveAt_InterpolatesBetweenSamples()
        {
            var panes = CreatePanes();
            panes.UpdateLive(Lap);

            Assert.True(panes.TryGetLiveAt(150, out LiveCursorSample sample));
            Assert.Equal(210, sample.Speed, 3);
            Assert.Equal(75, sample.Throttle, 3);
            Assert.Equal(-0.05, sample.DeltaSeconds, 3);
        }

        [Fact]
        public void TryGetLiveAt_ExactSampleAndOutOfRange()
        {
            var panes = CreatePanes();
            panes.UpdateLive(Lap);

            Assert.True(panes.TryGetLiveAt(300, out LiveCursorSample last));
            Assert.Equal(240, last.Speed, 3);

            Assert.False(panes.TryGetLiveAt(50, out _));    // before the first live sample
            Assert.False(panes.TryGetLiveAt(301, out _));   // the car has not got there yet
        }

        [Fact]
        public void TryGetLiveAt_FalseWithNoLiveData()
        {
            Assert.False(CreatePanes().TryGetLiveAt(100, out _));
        }

        [Fact]
        public void Hover_ShowsDashedCursorAndReadout()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                panes.UpdateLive(Lap);

                cursor.Hover(150);

                Assert.True(panes.IsCursorVisible);
                Assert.Equal(150, panes.CursorDistance);
                Assert.True(vm.Cursor.IsVisible);
                Assert.False(vm.Cursor.IsPinned);
                Assert.Equal("@ 150 m", vm.Cursor.DistanceText);
                Assert.Equal("LIVE 210.0", vm.Cursor.LiveSpeedText);
                Assert.StartsWith("REF ", vm.Cursor.RefSpeedText);
                Assert.Equal("Δt -0.050s", vm.Cursor.DeltaText);
            }
        }

        [Fact]
        public void Hover_BeyondLiveData_ShowsReferenceOnly()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                panes.UpdateLive(Lap);

                cursor.Hover(3000);

                Assert.Equal("LIVE --", vm.Cursor.LiveSpeedText);
                Assert.Equal("Δt --", vm.Cursor.DeltaText);
                Assert.Matches(@"^REF \d+\.\d km/h$", vm.Cursor.RefSpeedText);
            }
        }

        [Fact]
        public void Leave_WithoutPin_HidesCursor()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Hover(500);
                cursor.Leave();

                Assert.False(panes.IsCursorVisible);
                Assert.False(vm.Cursor.IsVisible);
                Assert.Equal("", vm.Cursor.DistanceText);
            }
        }

        [Fact]
        public void Click_DropsPin_ThatStaysAfterLeaving()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Hover(1200);
                cursor.Click(1200);
                cursor.Leave();

                Assert.Equal([1200.0], cursor.Pins);
                Assert.Equal(1, panes.PinCount);
                Assert.False(panes.IsCursorVisible); // dashed hover line is gone, the pin draws its own
                Assert.Equal("@ 1,200 m PINNED", vm.Cursor.DistanceText);
            }
        }

        [Fact]
        public void HoveringElsewhere_FollowsMouse_ThenReadoutReturnsToPin()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1200);

                cursor.Hover(2500, snapRadiusMeters: 20);
                Assert.Equal(2500, panes.CursorDistance);
                Assert.False(vm.Cursor.IsPinned);

                cursor.Leave();
                Assert.Equal("@ 1,200 m PINNED", vm.Cursor.DistanceText);
            }
        }

        [Fact]
        public void Hover_NearPin_SnapsOntoIt()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1000);

                cursor.Hover(1015, snapRadiusMeters: 20);
                Assert.True(cursor.IsHoverOnPin);
                Assert.Equal(1000, panes.CursorDistance);
                Assert.Equal("@ 1,000 m PINNED", vm.Cursor.DistanceText);

                cursor.Hover(1050, snapRadiusMeters: 20);
                Assert.False(cursor.IsHoverOnPin);
                Assert.Equal(1050, panes.CursorDistance);
            }
        }

        [Fact]
        public void Hover_SnapsToTheNearestOfTwoPins()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1000);
                cursor.Click(1030);

                cursor.Hover(1022, snapRadiusMeters: 50);

                Assert.Equal(1030, panes.CursorDistance);
            }
        }

        [Fact]
        public void MultiplePins_ReadoutShowsTheLatestWhenMouseIsAway()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1000);
                cursor.Click(2000);
                cursor.Leave();

                Assert.Equal([1000.0, 2000.0], cursor.Pins);
                Assert.Equal(2, panes.PinCount);
                Assert.Equal("@ 2,000 m PINNED", vm.Cursor.DistanceText);
            }
        }

        [Fact]
        public void ClickingSnappedPin_RemovesOnlyThatPin()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1000);
                cursor.Click(2000);

                cursor.Click(1010, snapRadiusMeters: 20);

                Assert.Equal([2000.0], cursor.Pins);
                Assert.Equal(1, panes.PinCount);
            }
        }

        [Fact]
        public void ClickingExactPin_WithoutSnapRadius_RemovesIt()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1200);
                cursor.Click(1200);
                cursor.Leave();

                Assert.Empty(cursor.Pins);
                Assert.Equal(0, panes.PinCount);
                Assert.False(vm.Cursor.IsVisible);
            }
        }

        [Fact]
        public void PinDots_SitOnTheLiveTraces()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                panes.UpdateLive(Lap);

                cursor.Click(150);

                Coordinates[] dots = panes.GetPinDots(0);
                Assert.Equal(new Coordinates(150, 210), dots[0]);           // live speed
                Assert.Equal(75, dots[1].Y, 3);                              // live throttle
                Assert.Equal(-0.05, dots[2].Y, 3);                           // live delta
            }
        }

        [Fact]
        public void PinDots_FallBackToReference_WhereTheCarHasNotBeen()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                panes.UpdateLive(Lap);

                cursor.Click(3000);

                var reference = vm.ReferenceLap!.GetSampleAtDistance(3000f);
                Coordinates[] dots = panes.GetPinDots(0);
                Assert.Equal(reference.SpeedKmh, dots[0].Y, 3);
                Assert.Equal(reference.Throttle, dots[1].Y, 3);
                Assert.Equal(0, dots[2].Y);                                  // no live delta yet
            }
        }

        [Fact]
        public void PinnedReadoutAndDot_UpdateAsLiveDataArrives()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(250);
                cursor.Leave();
                Assert.Equal("LIVE --", vm.Cursor.LiveSpeedText);

                panes.UpdateLive(Lap);   // the car drives past the pin
                cursor.Refresh();         // done every frame by MainWindow.RefreshPlots

                Assert.Equal("LIVE 230.0", vm.Cursor.LiveSpeedText);
                Assert.Equal(230, panes.GetPinDots(0)[0].Y, 3);
            }
        }

        [Fact]
        public void Hover_ClampsToTheLap()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Hover(-100);
                Assert.Equal(0, panes.CursorDistance);

                cursor.Hover(99999);
                Assert.Equal(vm.ReferenceLap!.TrackLengthMeters, panes.CursorDistance);
            }
        }

        [Fact]
        public void Clear_RemovesEveryPin()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1000);
                cursor.Click(2000);

                cursor.Clear();

                Assert.Empty(cursor.Pins);
                Assert.Equal(0, panes.PinCount);
                Assert.False(panes.IsCursorVisible);
                Assert.False(vm.Cursor.IsVisible);
            }
        }

        [Fact]
        public void Render_WithPinsAndHoverCursor_DrawsWithoutErrors()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                panes.UpdateLive(Lap);
                cursor.Click(150);
                cursor.Click(3000);
                cursor.Hover(1200);

                foreach (var plot in new[] { panes.SpeedPlot, panes.PedalPlot, panes.DeltaPlot })
                {
                    Assert.Equal(600, plot.GetImage(600, 200).Width);
                }
            }
        }

        [Fact]
        public void ResetLap_RaisesLapReset_SoTheWindowCanClearPins()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            using var vm = new WorkbenchViewModel(library: temp.Library);
            int resets = 0;
            vm.LapReset += () => resets++;

            vm.ResetLap();

            Assert.Equal(1, resets);
        }
    }
}
