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
        public void Click_PinsCursor_ThatStaysAfterLeaving()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Hover(1200);
                cursor.Click(1200);
                cursor.Leave();

                Assert.True(cursor.IsPinned);
                Assert.True(panes.IsCursorVisible);
                Assert.Equal(1200, panes.CursorDistance);
                Assert.Equal("@ 1,200 m PINNED", vm.Cursor.DistanceText);
            }
        }

        [Fact]
        public void HoveringElsewhere_WhilePinned_FollowsMouse_ThenReturnsToPin()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1200);

                cursor.Hover(2500);
                Assert.Equal(2500, panes.CursorDistance);
                Assert.False(vm.Cursor.IsPinned);

                cursor.Leave();
                Assert.Equal(1200, panes.CursorDistance);
                Assert.True(vm.Cursor.IsPinned);
            }
        }

        [Fact]
        public void SecondClick_ReleasesPin()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1200);
                cursor.Click(1200);
                cursor.Leave();

                Assert.False(cursor.IsPinned);
                Assert.False(panes.IsCursorVisible);
            }
        }

        [Fact]
        public void PinnedReadout_UpdatesAsLiveDataArrives()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(250);
                Assert.Equal("LIVE --", vm.Cursor.LiveSpeedText);

                panes.UpdateLive(Lap);   // the car drives past the pinned distance
                cursor.Refresh();         // done every frame by MainWindow.RefreshPlots

                Assert.Equal("LIVE 230.0", vm.Cursor.LiveSpeedText);
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
        public void Clear_RemovesPinnedCursor()
        {
            var (temp, vm, panes, cursor) = Create();
            using (temp) using (vm)
            {
                cursor.Click(1200);

                cursor.Clear();

                Assert.False(cursor.IsPinned);
                Assert.False(panes.IsCursorVisible);
                Assert.False(vm.Cursor.IsVisible);
            }
        }
    }
}
