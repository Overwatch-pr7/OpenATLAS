using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;
using OpenATLAS.Core.Engine;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Import;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the two-racer setup: Racer 1 from simulation / saved lap, Racer 2 from F1 or My Laps,
    /// and the File > Import Lap flow.
    /// </summary>
    public class TwoRacerTests
    {
        private const string GrandPrix = "Italian Grand Prix";

        // Monza reference (HAM) plus the same lap saved as one of the user's laps ("Mine")
        private static TempReferenceLibrary CreateLibrary(bool withUserLap = true)
        {
            var temp = new TempReferenceLibrary($"2021/{GrandPrix}/HAM");
            TrackLayoutTests.WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", GrandPrix));
            if (withUserLap)
            {
                string folder = Path.Combine(temp.RootPath, ReferenceLibrary.UserLapsFolderName, GrandPrix);
                Directory.CreateDirectory(folder);
                File.Copy(TempReferenceLibrary.SourceLapPath, Path.Combine(folder, "Mine.parquet"));
            }
            return temp;
        }

        private static void WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "Timed out waiting for playback");
                Thread.Sleep(10);
            }
        }

        [Fact]
        public void Racer1_DefaultsToSimulation_AndStartRunsIt()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            Assert.Equal(WorkbenchViewModel.Racer1Simulation, vm.SelectedRacer1Source);

            vm.StartRacer1();
            Assert.True(vm.IsRunning);
            Assert.Equal(WorkbenchViewModel.SourceSimulation, vm.DataSource);

            vm.StopRacer1();
            Assert.False(vm.IsRunning);
        }

        [Fact]
        public void SavedLaps_ListTheUserLapsForTheSelectedGrandPrix()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Equal(["Mine"], vm.AvailableSavedLaps);
            Assert.Equal("Mine", vm.SelectedSavedLap);
        }

        [Fact]
        public void SavedLapReplay_OfTheReferenceLapItself_HasNoDelta()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedRacer1Source = WorkbenchViewModel.Racer1SavedLap;

            vm.StartRacer1();
            Assert.Equal("SAVED LAP: Mine", vm.DataSource);
            Assert.Equal("SAVED LAP REPLAY", vm.ConnectionStatus);
            WaitFor(() => vm.CircularBuffer.Count >= 60); // about 1 s of playback
            vm.StopRacer1();

            Assert.True(vm.CircularBuffer.TryPeekLatest(out LiveDeltaSnapshot latest));
            Assert.True(latest.Distance > 60, "the replay should be moving along the lap");
            Assert.InRange(latest.DeltaTime, -0.05f, 0.05f);  // same lap on both sides
            Assert.InRange(latest.SimSpeed - latest.RefSpeed, -1f, 1f);
        }

        [Fact]
        public void SavedLapReplay_CanBePaused()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.SelectedRacer1Source = WorkbenchViewModel.Racer1SavedLap;
            vm.StartRacer1();

            vm.TogglePauseSimulation();

            Assert.True(vm.IsSimulationPaused);
            Assert.Equal("SAVED LAP PAUSED", vm.ConnectionStatus);
            vm.StopRacer1();
        }

        [Fact]
        public void Start_IsDisabledForSavedLapSourceWithoutLaps()
        {
            using var temp = CreateLibrary(withUserLap: false);
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.SelectedRacer1Source = WorkbenchViewModel.Racer1SavedLap;

            Assert.Empty(vm.AvailableSavedLaps);
            Assert.False(vm.StartRacer1Command.CanExecute(null));
        }

        [Fact]
        public void Racer1Source_IsLockedWhileRunning()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.StartRacer1();
            Assert.False(vm.CanChangeReference);
            Assert.False(vm.StartRacer1Command.CanExecute(null));
            Assert.True(vm.StopRacer1Command.CanExecute(null));
            vm.StopRacer1();
        }

        [Fact]
        public void MyLaps_AreSelectableAsRacer2_WithTheGrandPrixLayout()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Equal(["2021", ReferenceLibrary.UserLapsFolderName], vm.AvailableYears);

            vm.SelectedYear = ReferenceLibrary.UserLapsFolderName;

            Assert.Equal(GrandPrix, vm.SelectedTrack);
            Assert.Equal("Mine", vm.SelectedDriver);
            Assert.True(vm.HasSectors);  // track.json copied/available for My Laps
        }

        [Fact]
        public void PlaybackDrivers_MapSavedLapsOntoTheReferenceByLapFraction()
        {
            var lap = ReferenceLapIndex.FromParquetFile(TempReferenceLibrary.SourceLapPath);
            var driver = new SavedLapDriver(lap, referenceLengthMeters: lap.TrackLengthMeters * 2f);

            PlaybackSample sample = default;
            for (int i = 0; i < 600; i++) sample = driver.Next(1f / 60f); // 10 s

            float expected = lap.GetDistanceAtTime(10f) * 2f;
            Assert.Equal(expected, sample.Distance, 0);
        }

        // ---------------------------------------------------------------- File > Import Lap

        private static string WriteCsv(string csv)
        {
            string path = Path.Combine(Path.GetTempPath(), $"openatlas-import-{Guid.NewGuid():N}.csv");
            File.WriteAllText(path, csv);
            return path;
        }

        [Fact]
        public void Import_LoadsTheFastestLap_AndSavesItToMyLaps()
        {
            using var temp = CreateLibrary(withUserLap: false);
            using var vm = new WorkbenchViewModel(library: temp.Library);
            string csv = WriteCsv(LapImportTests.MultiLapCsv());
            try
            {
                var import = new ImportLapViewModel(vm);
                Assert.Equal(GrandPrix, import.SelectedGrandPrix); // defaults to Racer 2's Grand Prix

                import.LoadFile(csv);
                Assert.True(import.IsLoaded);
                Assert.Equal("", import.Error);
                Assert.Equal(4, import.Laps.Count);
                Assert.Equal(3, import.SelectedLap!.Lap.Number);
                Assert.Contains("(out/in lap)", import.Laps[0].Label);
                Assert.Equal($"{Path.GetFileNameWithoutExtension(csv)} L3", import.LapName);

                import.LapName = "Test lap";
                string? saved = import.Import();

                Assert.Equal("Test lap", saved);
                Assert.Equal(["Test lap"], vm.AvailableSavedLaps);
                Assert.Equal("Test lap", vm.SelectedSavedLap);
                Assert.Contains(ReferenceLibrary.UserLapsFolderName, vm.AvailableYears);
                Assert.Equal("HAM", vm.SelectedDriver); // Racer 2 selection untouched
            }
            finally
            {
                File.Delete(csv);
            }
        }

        [Fact]
        public void Import_ReportsUnreadableFiles()
        {
            using var temp = CreateLibrary(withUserLap: false);
            using var vm = new WorkbenchViewModel(library: temp.Library);
            string csv = WriteCsv("a,b,c\n1,2,3\n");
            try
            {
                var import = new ImportLapViewModel(vm);
                import.LoadFile(csv);

                Assert.False(import.IsLoaded);
                Assert.Contains("required columns", import.Error);
                Assert.False(import.CanImport);
                Assert.Null(import.Import());
            }
            finally
            {
                File.Delete(csv);
            }
        }

        [Fact]
        public void FormatDescription_ListsRequiredColumnsAndSupportedTools()
        {
            Assert.Contains("REQUIRED COLUMNS", ImportLapViewModel.FormatDescription);
            Assert.Contains("MoTeC i2", ImportLapViewModel.FormatDescription);
            Assert.Contains("Telemetrick", ImportLapViewModel.FormatDescription);
        }
    }
}
