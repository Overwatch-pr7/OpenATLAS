using System;
using System.Diagnostics;
using System.Threading;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Tests.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// Verifies the status bar data source and pausing/resuming the simulated lap.
    /// </summary>
    public class SimulationControlTests
    {
        private static TempReferenceLibrary CreateLibrary() => new("2021/Italian Grand Prix/HAM");

        // Waits (up to 3 s) for the simulation task to push samples into the ring buffer
        private static void WaitFor(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "Timed out waiting for the simulation");
                Thread.Sleep(10);
            }
        }

        [Fact]
        public void DataSource_DefaultsToNotAvailable()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            Assert.Equal(WorkbenchViewModel.SourceNone, vm.DataSource);
            Assert.Equal("N/A", vm.DataSource);
        }

        [Fact]
        public void DataSource_ShowsSimulationWhileRunning_AndResetsOnStop()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.StartSimulationReplay();
            Assert.Equal(WorkbenchViewModel.SourceSimulation, vm.DataSource);

            vm.StopSimulationReplay();
            Assert.Equal(WorkbenchViewModel.SourceNone, vm.DataSource);
        }

        [Fact]
        public void DataSource_StaysNotAvailableWhileWaitingForAc()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            // AC is not running during tests, so the listener only waits for it
            vm.StartLiveIngestion();
            Assert.True(vm.IsLiveConnected);
            Assert.Equal(WorkbenchViewModel.SourceNone, vm.DataSource);

            vm.StopLiveIngestion();
            Assert.Equal(WorkbenchViewModel.SourceNone, vm.DataSource);
        }

        [Fact]
        public void Pause_FreezesTheSimulation_AndResumeContinuesFromTheSamePoint()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.StartSimulationReplay();
            WaitFor(() => vm.CircularBuffer.Count >= 5);

            vm.TogglePauseSimulation();
            Assert.True(vm.IsSimulationPaused);
            Assert.Equal("SIMULATION PAUSED", vm.ConnectionStatus);
            Assert.Equal(WorkbenchViewModel.SourceSimulation, vm.DataSource);

            Thread.Sleep(50); // let a tick that was already in progress finish
            long pausedVersion = vm.CircularBuffer.Version;
            Assert.True(vm.CircularBuffer.TryPeekLatest(out LiveDeltaSnapshot pausedAt));

            Thread.Sleep(200);
            Assert.Equal(pausedVersion, vm.CircularBuffer.Version);

            vm.TogglePauseSimulation();
            Assert.False(vm.IsSimulationPaused);
            WaitFor(() => vm.CircularBuffer.Version > pausedVersion);

            Assert.True(vm.CircularBuffer.TryPeekLatest(out LiveDeltaSnapshot resumedAt));
            Assert.True(resumedAt.Distance > pausedAt.Distance, "Resume should continue forward, not restart the lap");
            Assert.True(resumedAt.Distance - pausedAt.Distance < 50f, "Resume should not jump ahead");

            vm.StopSimulationReplay();
        }

        [Fact]
        public void Stop_WhilePaused_ClearsThePausedState()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);
            vm.StartSimulationReplay();
            vm.TogglePauseSimulation();

            vm.StopSimulationReplay();

            Assert.False(vm.IsSimulationPaused);
            Assert.False(vm.PauseSimulationCommand.CanExecute(null));
        }

        [Fact]
        public void Pause_DoesNothingWhenNoSimulationIsRunning()
        {
            using var temp = CreateLibrary();
            using var vm = new WorkbenchViewModel(library: temp.Library);

            vm.TogglePauseSimulation();

            Assert.False(vm.IsSimulationPaused);
            Assert.Equal("STANDBY", vm.ConnectionStatus);
        }
    }
}
