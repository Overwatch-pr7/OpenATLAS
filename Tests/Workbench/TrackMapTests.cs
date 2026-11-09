using System.Collections.Generic;
using System.IO;
using Xunit;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.Plotting;
using OpenATLAS.Workbench.ViewModels;
using ScottPlot;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the track map: outline lookup, map markers, and the sidebar sizing/visibility state.
    /// </summary>
    public class TrackMapTests
    {
        // 400 m square circuit, anticlockwise from (0,0): east, north, west, south. Sectors of 100/200/100 m.
        private static TrackLayout SquareLayout(bool withLabels = true)
        {
            var outline = new List<float[]>();
            for (int d = 0; d <= 400; d += 10)
            {
                float x = d <= 100 ? d : d <= 200 ? 100 : d <= 300 ? 300 - d : 0;
                float y = d <= 100 ? 0 : d <= 200 ? d - 100 : d <= 300 ? 100 : 400 - d;
                outline.Add([d, x, y]);
            }

            return new TrackLayout
            {
                Event = "Square Grand Prix",
                LengthMeters = 400,
                Sectors = [new TrackSector(1, 0, 100), new TrackSector(2, 100, 300), new TrackSector(3, 300, 400)],
                Corners =
                [
                    new TrackCorner(1, "", 100, "", withLabels ? 110 : null, withLabels ? -10 : null),
                    new TrackCorner(2, "", 200, "", withLabels ? 110 : null, withLabels ? 110 : null),
                ],
                Outline = [.. outline],
            };
        }

        [Fact]
        public void PointAtFraction_FollowsTheOutline()
        {
            TrackLayout layout = SquareLayout();

            Assert.Equal((0.0, 0.0), layout.PointAtFraction(0));
            Assert.Equal((50.0, 0.0), layout.PointAtFraction(0.125));     // halfway along the first side
            Assert.Equal((100.0, 50.0), layout.PointAtFraction(0.375));
            Assert.Equal((100.0, 0.0), layout.PointAtFraction(1.25));     // wraps onto the next lap
        }

        [Fact]
        public void PointAtFraction_InterpolatesBetweenOutlinePoints()
        {
            var (x, y) = SquareLayout().PointAtFraction(15.0 / 400);

            Assert.Equal(15, x, 3);
            Assert.Equal(0, y, 3);
        }

        [Fact]
        public void MapAspectRatio_IsHeightOverWidth()
        {
            Assert.Equal(1.0, SquareLayout().MapAspectRatio, 3);
            Assert.False(new TrackLayout().HasOutline);
        }

        [Fact]
        public void Load_DrawsTheMap_AndNullClearsIt()
        {
            var map = new TrackMap(new Plot());

            map.Load(SquareLayout());
            Assert.True(map.HasMap);

            map.Load(null);
            Assert.False(map.HasMap);
        }

        [Fact]
        public void Load_LayoutWithoutOutline_HasNoMap()
        {
            var map = new TrackMap(new Plot());

            map.Load(new TrackLayout { Sectors = [new TrackSector(1, 0, 100)] });

            Assert.False(map.HasMap);
        }

        [Fact]
        public void SetCar_PlacesTheCarAndHighlightsItsSector()
        {
            var map = new TrackMap(new Plot());
            map.Load(SquareLayout());

            map.SetCar(0.5); // 200 m: second sector, top-right corner
            Assert.Equal(new Coordinates(100, 100), map.CarPosition);
            Assert.Equal(1, map.ActiveSector);

            map.SetCar(0.9); // 360 m: third sector
            Assert.Equal(2, map.ActiveSector);

            map.SetCar(null);
            Assert.Null(map.CarPosition);
            Assert.Equal(-1, map.ActiveSector);
        }

        [Fact]
        public void SetHoverAndPins_MarkTheirPositions()
        {
            var map = new TrackMap(new Plot());
            map.Load(SquareLayout());

            map.SetHover(0.25);
            map.SetPins([0.125, 0.625]);
            Assert.Equal(new Coordinates(100, 0), map.HoverPosition);
            Assert.Equal(2, map.PinCount);

            map.SetHover(null);
            map.SetPins([0.125]);
            Assert.Null(map.HoverPosition);
            Assert.Equal(1, map.PinCount);
        }

        [Fact]
        public void Render_WithCarPinsAndLabels_DrawsWithoutErrors()
        {
            foreach (bool withLabels in new[] { true, false })
            {
                var map = new TrackMap(new Plot());
                map.Load(SquareLayout(withLabels));
                map.SetCar(0.3);
                map.SetHover(0.6);
                map.SetPins([0.1]);

                Assert.Equal(300, map.Plot.GetImage(300, 300).Width);
            }
        }

        [Fact]
        public void MapHeight_ScalesWithTheTrackShape_WithinLimits()
        {
            Assert.Equal(WorkbenchViewModel.TrackMapWidth, WorkbenchViewModel.MapHeightFor(SquareLayout()), 3);
            Assert.Equal(WorkbenchViewModel.TrackMapMinHeight, WorkbenchViewModel.MapHeightFor(null));

            var flat = new TrackLayout { Outline = [[0, 0, 0], [10, 1000, 100]] };   // 0.1 tall
            var tall = new TrackLayout { Outline = [[0, 0, 0], [10, 100, 1000]] };   // 10x tall
            Assert.Equal(WorkbenchViewModel.TrackMapMinHeight, WorkbenchViewModel.MapHeightFor(flat));
            Assert.Equal(WorkbenchViewModel.TrackMapMaxHeight, WorkbenchViewModel.MapHeightFor(tall));
        }

        [Fact]
        public void SelectingGrandPrix_ExposesItsLayoutForTheMap()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM", "2021/Bahrain Grand Prix/VER");
            TrackLayoutTests.WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", "Italian Grand Prix"));
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.SelectedTrack = "Italian Grand Prix";
            Assert.NotNull(vm.TrackLayout);
            Assert.False(vm.HasTrackMap); // this test layout has sectors but no outline

            vm.SelectedTrack = "Bahrain Grand Prix";
            Assert.Null(vm.TrackLayout);
            Assert.Equal(WorkbenchViewModel.TrackMapMinHeight, vm.TrackMapHeight);
        }

        [Fact]
        public void ToggleTrackMapCommand_HidesAndShowsTheMap()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            using var vm = new WorkbenchViewModel(library: temp.Library);
            Assert.True(vm.IsTrackMapVisible);

            vm.ToggleTrackMapCommand.Execute(null);
            Assert.False(vm.IsTrackMapVisible);

            vm.ToggleTrackMapCommand.Execute(null);
            Assert.True(vm.IsTrackMapVisible);
        }

        [Fact]
        public void TrackLibrary_EveryGrandPrixHasAMapWithLabelledCorners()
        {
            var library = ReferenceLibrary.Find(System.AppContext.BaseDirectory)!;

            foreach (string track in library.GetTracks("2021"))
            {
                TrackLayout layout = library.GetTrackLayout("2021", track)!;
                Assert.True(layout.HasOutline, $"{track} has no map outline");
                Assert.InRange(layout.OutlineLengthMeters, layout.LengthMeters * 0.95, layout.LengthMeters * 1.05);
                Assert.All(layout.Corners, c => Assert.True(c.LabelX.HasValue && c.LabelY.HasValue, $"{track} T{c.Number}{c.Letter} has no label position"));
            }
        }
    }
}
