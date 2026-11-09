using System.IO;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the TRACK SECTOR ANALYSIS panel follows the selected Grand Prix and
    /// accumulates live per-sector deltas.
    /// </summary>
    public class SectorAnalysisTests
    {
        // Two Grands Prix: Monza has a track.json, Bahrain does not
        private static TempReferenceLibrary CreateLibrary()
        {
            var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM", "2021/Bahrain Grand Prix/VER");
            TrackLayoutTests.WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", "Italian Grand Prix"));
            return temp;
        }

        private static LiveDeltaSnapshot At(float distance, float delta) =>
            new(distance, 300f, 300f, 100f, 100f, 0f, 0f, 7, 7, 11000, 11000, 10f, 10f, delta);

        private static WorkbenchViewModel CreateMonzaViewModel(TempReferenceLibrary temp)
        {
            var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedTrack = "Italian Grand Prix";
            return vm;
        }

        [Fact]
        public void SelectingGrandPrix_BuildsSectorsFromItsTrackJson()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);

            Assert.True(vm.HasSectors);
            Assert.Equal(3, vm.Sectors.Count);
            Assert.Equal("SECTOR 1: PRIMA VARIANTE", vm.Sectors[0].Title);
            Assert.Equal("T1-T3 | 0 - 1,800 m", vm.Sectors[0].Detail);
            Assert.Equal("SECTOR 2", vm.Sectors[1].Title);
            Assert.Equal("SECTOR 3: PARABOLICA", vm.Sectors[2].Title);
            Assert.Equal("T11 | 3,700 - 5,763 m", vm.Sectors[2].Detail);
        }

        [Fact]
        public void SectorReferenceTimes_ComeFromTheReferenceLap()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);

            // Fixture lap (HAM Monza 2023): -0.0007s at 0 m, 25.722s at 1800 m, 53.935s at 3700 m, 80.811s at the line
            Assert.Equal("REF 25.723s", vm.Sectors[0].ReferenceTimeString);
            Assert.Equal("REF 28.212s", vm.Sectors[1].ReferenceTimeString);
            Assert.Equal("REF 26.876s", vm.Sectors[2].ReferenceTimeString);
        }

        [Fact]
        public void SwitchingToGrandPrixWithoutTrackJson_EmptiesThePanel()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);

            vm.SelectedTrack = "Bahrain Grand Prix";

            Assert.False(vm.HasSectors);
            Assert.Empty(vm.Sectors);
        }

        [Fact]
        public void LiveDeltas_AccumulatePerSector()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);

            vm.UpdateSnapshot(At(100f, 0.00f));
            vm.UpdateSnapshot(At(1700f, 0.10f));   // +0.10s gained so far in S1
            Assert.Equal("+0.100s", vm.Sectors[0].DeltaString);
            Assert.True(vm.Sectors[0].IsActive);
            Assert.False(vm.Sectors[1].HasDelta);

            vm.UpdateSnapshot(At(1850f, 0.12f));   // S1 closes at +0.12, S2 starts from there
            vm.UpdateSnapshot(At(3600f, -0.08f));  // lost 0.20s in S2

            Assert.Equal("+0.120s", vm.Sectors[0].DeltaString);
            Assert.True(vm.Sectors[0].IsAhead);
            Assert.False(vm.Sectors[0].IsActive);
            Assert.Equal("-0.200s", vm.Sectors[1].DeltaString);
            Assert.False(vm.Sectors[1].IsAhead);
            Assert.True(vm.Sectors[1].IsActive);
            Assert.Equal("--", vm.Sectors[2].DeltaString);
        }

        [Fact]
        public void NewLap_ClearsSectorDeltas()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);
            vm.UpdateSnapshot(At(100f, 0f));
            vm.UpdateSnapshot(At(4000f, 0.3f));

            vm.UpdateSnapshot(At(20f, 0.01f)); // crossed the line

            Assert.Equal("+0.010s", vm.Sectors[0].DeltaString);
            Assert.False(vm.Sectors[1].HasDelta);
            Assert.False(vm.Sectors[2].HasDelta);
        }

        [Fact]
        public void JoiningMidLap_LeavesEarlierSectorsEmpty()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);

            vm.UpdateSnapshot(At(2500f, 0.40f));
            vm.UpdateSnapshot(At(3000f, 0.45f));

            Assert.False(vm.Sectors[0].HasDelta);
            Assert.Equal("+0.050s", vm.Sectors[1].DeltaString);
        }

        [Fact]
        public void ResetLap_ClearsSectorDeltas()
        {
            using var temp = CreateLibrary();
            using var vm = CreateMonzaViewModel(temp);
            vm.UpdateSnapshot(At(100f, 0f));
            vm.UpdateSnapshot(At(2000f, 0.2f));

            vm.ResetLap();

            Assert.All(vm.Sectors, s => Assert.False(s.HasDelta));
            Assert.All(vm.Sectors, s => Assert.False(s.IsActive));
        }
    }
}
