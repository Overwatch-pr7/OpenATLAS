using Xunit;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the Year -> Track -> Driver reference lap dropdowns in the WorkbenchViewModel.
    /// </summary>
    public class ReferenceSelectionTests
    {
        private static TempReferenceLibrary CreateLibrary() => new(
            "2023/Monza/HAM",
            "2023/Monza/VER",
            "2023/Silverstone/NOR",
            "2024/Monza/LEC",
            "2024/Monza/VER");

        [Fact]
        public void Startup_SelectsNewestYearFirstTrackFirstDriver_AndLoadsLap()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Equal(["2024", "2023"], vm.AvailableYears);
            Assert.Equal("2024", vm.SelectedYear);
            Assert.Equal("Monza", vm.SelectedTrack);
            Assert.Equal("LEC", vm.SelectedDriver);
            Assert.NotNull(vm.ReferenceLap);
            Assert.Equal("LEC", vm.ReferenceDriverName);
            Assert.Equal("LEC 2024 REF", vm.ReferenceLabel);
        }

        [Fact]
        public void ChangingYear_KeepsTrackAndDriverWhenAvailable_AndReloadsLap()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedDriver = "VER";
            var lap2024 = vm.ReferenceLap;
            int reloads = 0;
            vm.ReferenceLapChanged += () => reloads++;

            vm.SelectedYear = "2023";

            Assert.Equal(["Monza", "Silverstone"], vm.AvailableTracks);
            Assert.Equal("Monza", vm.SelectedTrack);
            Assert.Equal("VER", vm.SelectedDriver);
            Assert.Equal(1, reloads);
            Assert.NotSame(lap2024, vm.ReferenceLap);
        }

        [Fact]
        public void ChangingTrack_FallsBackToFirstDriverWhenCurrentIsMissing()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedYear = "2023";

            vm.SelectedTrack = "Silverstone";

            Assert.Equal(["NOR"], vm.AvailableDrivers);
            Assert.Equal("NOR", vm.SelectedDriver);
            Assert.Equal("Silverstone", vm.ReferenceTrackName);
        }

        [Fact]
        public void ChangingDriver_ResetsLiveSession()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.UpdateSnapshot(new OpenATLAS.Core.Models.LiveDeltaSnapshot(
                1000f, 300f, 300f, 100f, 100f, 0f, 0f, 7, 7, 11000, 11000, 15f, 15f, 0.2f));

            vm.SelectedDriver = "VER";

            Assert.Equal(0f, vm.CurrentDistanceMeters);
            Assert.Equal("+0.000s", vm.DeltaDisplayString);
        }

        [Fact]
        public void EmptyLibrary_LeavesNoReferenceLoaded()
        {
            using var temp = new TempReferenceLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Empty(vm.AvailableYears);
            Assert.Null(vm.SelectedYear);
            Assert.Null(vm.ReferenceLap);
        }

        [Fact]
        public void ReferenceSelection_IsLockedDuringSimulation()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            Assert.True(vm.CanChangeReference);

            vm.StartSimulationReplay();
            Assert.False(vm.CanChangeReference);

            vm.StopSimulationReplay();
            Assert.True(vm.CanChangeReference);
        }
    }
}
