using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using OpenATLAS.Core.Import;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies REC / SAVE: laps are captured in memory with a reason when skipped,
    /// and only reach My Laps when the user saves them.
    /// </summary>
    public class RecordingTests
    {
        private const string GrandPrix = "Italian Grand Prix";

        // Live-style samples: AC lap clock from 0, no distance channel, 50 m/s for the given time
        private static List<RawLapSample> LiveLap(double seconds, double startTime = 0)
        {
            var samples = new List<RawLapSample>();
            for (int i = 0; i <= seconds * 20; i++)
            {
                samples.Add(new RawLapSample(startTime + i / 20.0, double.NaN, 180, 100, 0, 6, 10000));
            }
            return samples;
        }

        private static TempReferenceLibrary CreateLibrary() => new($"2021/{GrandPrix}/HAM");

        // ---------------------------------------------------------------- Recorder reasons

        [Fact]
        public void Recorder_ExplainsWhyALapWasSkipped()
        {
            var recorder = new LapRecorder();

            foreach (var s in LiveLap(30, startTime: 40)) recorder.Add(s);   // REC switched on mid-lap
            Assert.Null(recorder.FinishLap(3000, out string? midLap));
            Assert.Contains("partway", midLap);

            foreach (var s in LiveLap(20)) recorder.Add(s);                  // 1,000 m: pitted
            Assert.Null(recorder.FinishLap(3000, out string? short_));
            Assert.Contains("only 1,000 m", short_);

            foreach (var s in LiveLap(60)) recorder.Add(s);                  // full 3,000 m lap
            Assert.NotNull(recorder.FinishLap(3000, out string? none));
            Assert.Null(none);
        }

        // ---------------------------------------------------------------- Capture and save

        [Fact]
        public void CapturedLaps_WaitInMemory_UntilSaved()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.IsRecordingLaps = true;

            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60), GrandPrix);

            Assert.True(vm.HasRecordedLaps);
            Assert.Equal("REC ON (1)", vm.RecordButtonText);
            Assert.Contains("press SAVE", vm.StatusMessage);
            Assert.False(Directory.Exists(Path.Combine(temp.RootPath, ReferenceLibrary.UserLapsFolderName)));
        }

        [Fact]
        public void SaveRecordedLap_WritesToMyLaps_AndLeavesTheWaitingList()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60), GrandPrix);

            string saved = vm.SaveRecordedLap(vm.RecordedLaps[0], "Practice run");

            Assert.Equal("Practice run", saved);
            Assert.False(vm.HasRecordedLaps);
            Assert.Equal("REC OFF", vm.RecordButtonText);
            Assert.Equal(["Practice run"], vm.AvailableSavedLaps);

            var lap = ReferenceLapIndex.FromParquetFile(temp.Library.GetLapPath(ReferenceLibrary.UserLapsFolderName, GrandPrix, saved));
            Assert.Equal(3000, lap.TrackLengthMeters);
        }

        [Fact]
        public void TurningRecOff_ReportsWaitingLaps_OrThatNoneWereCaptured()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.IsRecordingLaps = true;
            vm.IsRecordingLaps = false;
            Assert.Contains("No complete lap was captured", vm.StatusMessage);

            vm.IsRecordingLaps = true;
            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60), GrandPrix);
            vm.IsRecordingLaps = false;
            Assert.Contains("1 captured lap(s) waiting", vm.StatusMessage);
            Assert.True(vm.HasRecordedLaps); // turning REC off keeps captured laps
        }

        // ---------------------------------------------------------------- Floating widget actions

        [Fact]
        public void SaveLastRecordedLap_SavesTheMostRecentLapUnderItsSuggestedName()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60), GrandPrix);
            vm.AddRecordedLap(LiveLap(61), TimeSpan.FromSeconds(61.25), GrandPrix);
            Assert.True(vm.SaveLastRecordedLapCommand.CanExecute(null));

            string? saved = vm.SaveLastRecordedLap();

            Assert.EndsWith("1.01.250", saved);
            Assert.Single(vm.RecordedLaps);
            Assert.Equal(1, vm.RecordedLaps[0].Number); // the earlier lap is still waiting
        }

        [Fact]
        public void SaveLastRecordedLap_WithNothingCaptured_ExplainsWhy()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Null(vm.SaveLastRecordedLap());
            Assert.False(vm.SaveLastRecordedLapCommand.CanExecute(null));
            Assert.Contains("No captured lap", vm.StatusMessage);
        }

        [Fact]
        public void ToggleRacer1_StartsThenStops()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.ToggleRacer1();
            Assert.True(vm.IsRunning);

            vm.ToggleRacer1();
            Assert.False(vm.IsRunning);
        }

        [Fact]
        public void CurrentLapTimeString_FormatsTheLapClock()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.CurrentSimTimeSeconds = 81.345f;

            Assert.Equal("1:21.345", vm.CurrentLapTimeString);
        }

        // ---------------------------------------------------------------- Save dialog

        [Fact]
        public void SaveDialog_PreselectsTheFastestLap_WithASuggestedName()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.AddRecordedLap(LiveLap(62), TimeSpan.FromSeconds(62), GrandPrix);
            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60.5), GrandPrix);
            vm.AddRecordedLap(LiveLap(61), TimeSpan.FromSeconds(61), GrandPrix);

            var dialog = new SaveLapsViewModel(vm);

            Assert.Equal(2, dialog.SelectedLap!.Number);
            Assert.StartsWith("AC ", dialog.LapName);
            Assert.EndsWith("1.00.500", dialog.LapName);
            Assert.Equal($"LAP 2   1:00.500   {GrandPrix}", dialog.SelectedLap.Label);
        }

        [Fact]
        public void SaveDialog_SaveMovesOnToTheNextFastest_AndSaveAllClearsTheList()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.AddRecordedLap(LiveLap(62), TimeSpan.FromSeconds(62), GrandPrix);
            vm.AddRecordedLap(LiveLap(60), TimeSpan.FromSeconds(60), GrandPrix);
            vm.AddRecordedLap(LiveLap(61), TimeSpan.FromSeconds(61), GrandPrix);
            var dialog = new SaveLapsViewModel(vm);

            dialog.LapName = "Best";
            Assert.Equal("Best", dialog.Save());
            Assert.Equal(3, dialog.SelectedLap!.Number); // 61 s is now the fastest waiting

            Assert.Equal(2, dialog.SaveAll());
            Assert.False(vm.HasRecordedLaps);
            Assert.Null(dialog.SelectedLap);
            Assert.False(dialog.CanSaveAll);
            Assert.Equal(3, vm.AvailableSavedLaps.Count);
        }

        [Fact]
        public void SaveDialog_ReportsLapsThatCannotBeSaved()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.AddRecordedLap(LiveLap(10), TimeSpan.FromSeconds(10), GrandPrix); // 500 m: too short to normalize
            var dialog = new SaveLapsViewModel(vm);

            Assert.Null(dialog.Save());

            Assert.Contains("incomplete", dialog.Message);
            Assert.True(vm.HasRecordedLaps);
        }
    }
}
