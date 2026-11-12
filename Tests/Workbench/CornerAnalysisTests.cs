using System.IO;
using System.Linq;
using Xunit;
using OpenATLAS.Core.Analysis;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Analysis;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the corner analysis table (Task 5.2): the comparison wording and colours per corner,
    /// and the pop-up's choice of laps.
    /// </summary>
    public class CornerAnalysisTests
    {
        private const string GrandPrix = "Italian Grand Prix";

        private static CornerRowViewModel Row(LapTrace you, LapTrace reference)
        {
            var zone = CornerAnalyzer.BuildZones([new TrackCorner(1, "", 1000, "")], 2000)[0];
            return new CornerRowViewModel(zone, CornerAnalyzer.Measure(you, zone), CornerAnalyzer.Measure(reference, zone));
        }

        // ---------------------------------------------------------------- Rows

        [Fact]
        public void Row_SameDriving_IsOnPace()
        {
            var row = Row(CornerAnalyzerTests.OneCornerLap(), CornerAnalyzerTests.OneCornerLap());

            Assert.Equal("+0.000s", row.TimeDelta);
            Assert.Equal("SAME", row.BrakeDiff);
            Assert.Equal("ON PACE", row.Focus);
            Assert.Equal(CompareResult.Neutral, row.TimeResult);
        }

        [Fact]
        public void Row_BrakingEarly_LosesTime_AndSaysBrakeLater()
        {
            var row = Row(CornerAnalyzerTests.OneCornerLap(brakeAt: 790), CornerAnalyzerTests.OneCornerLap(brakeAt: 850));

            Assert.StartsWith("-", row.TimeDelta);
            Assert.Equal(CompareResult.Worse, row.TimeResult);
            Assert.Equal("790", row.BrakeYou);
            Assert.Equal("850", row.BrakeRef);
            Assert.Equal("60 EARLY", row.BrakeDiff);
            Assert.Equal(CompareResult.Worse, row.BrakeResult);
            Assert.Equal("BRAKE LATER", row.Focus);
        }

        [Fact]
        public void Row_LateOnTheThrottle_SaysEarlierThrottle()
        {
            var row = Row(CornerAnalyzerTests.OneCornerLap(throttleAt: 1150), CornerAnalyzerTests.OneCornerLap());

            Assert.Equal("130 LATE", row.ThrottleDiff);
            Assert.Equal(CompareResult.Worse, row.ThrottleResult);
            Assert.Equal("EARLIER THROTTLE", row.Focus);
        }

        [Fact]
        public void Row_BrakingLaterButSlowerAtTheApex_IsTooDeep()
        {
            var row = Row(CornerAnalyzerTests.OneCornerLap(brakeAt: 900, apexSpeed: 70), CornerAnalyzerTests.OneCornerLap());

            Assert.Equal("50 LATE", row.BrakeDiff);
            Assert.Equal("-30", row.SpeedDiff);
            Assert.Equal(CompareResult.Worse, row.SpeedResult);
            Assert.Equal("TOO DEEP ON ENTRY", row.Focus);
        }

        [Fact]
        public void Row_CornerNotReached_ShowsDashes()
        {
            var row = Row(CornerAnalyzerTests.OneCornerLap(coveredMeters: 500), CornerAnalyzerTests.OneCornerLap());

            Assert.Equal("--", row.SpeedYou);
            Assert.Equal("--", row.TimeDelta);
            Assert.Equal("", row.Focus);
        }

        // ---------------------------------------------------------------- Pop-up

        private static TempReferenceLibrary CreateLibrary()
        {
            var temp = new TempReferenceLibrary($"2021/{GrandPrix}/HAM", $"{ReferenceLibrary.UserLapsFolderName}/{GrandPrix}/Copy of HAM");
            TrackLayoutTests.WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", GrandPrix));
            return temp;
        }

        [Fact]
        public void Popup_ListsTheLapInProgressAndSavedLaps_AndComparesEveryCorner()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedTrack = GrandPrix;

            var corners = new CornerAnalysisViewModel(vm, _ => null);
            Assert.Equal([CornerAnalysisViewModel.CurrentLapOption, "SAVED: Copy of HAM"], corners.Racer1Options);

            corners.SelectedRacer1 = "SAVED: Copy of HAM";

            // Six corners in the test layout; the saved lap is the reference lap itself
            Assert.Equal(["T1", "T2", "T3", "T4", "T7", "T11"], corners.Rows.Select(r => r.Corner));
            Assert.All(corners.Rows, r => Assert.Equal("ON PACE", r.Focus));
            Assert.Contains("On pace everywhere", corners.Summary);
            Assert.Equal("", corners.Message);
            Assert.Equal("HAM 2021 REF", corners.Racer2Label);
        }

        [Fact]
        public void Popup_WithoutALapInProgress_ExplainsWhatToDo()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedTrack = GrandPrix;

            var corners = new CornerAnalysisViewModel(vm, _ => null) { SelectedRacer1 = CornerAnalysisViewModel.CurrentLapOption };

            Assert.Empty(corners.Rows);
            Assert.Contains("No lap in progress", corners.Message);
        }

        [Fact]
        public void Popup_PartialLap_FillsInTheCornersDrivenSoFar()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedTrack = GrandPrix;

            // The reference lap cut off at 1,600 m: T1 and T2 (zones end before 1,353 m) are complete
            var full = LapTrace.FromLap(vm.ReferenceLap!);
            int n = full.IndexAtOrAfter(1600);
            LapTrace partial = new(full.Distance[..n], full.Speed[..n], full.Throttle[..n], full.Brake[..n], full.Time[..n], full.LengthMeters);

            var corners = new CornerAnalysisViewModel(vm, _ => partial) { SelectedRacer1 = CornerAnalysisViewModel.CurrentLapOption };

            Assert.Equal("+0.000s", corners.Rows[0].TimeDelta);
            Assert.Equal("--", corners.Rows[3].SpeedYou);
            Assert.Contains("3 of 6 corners driven so far", corners.Message);
        }
    }
}
