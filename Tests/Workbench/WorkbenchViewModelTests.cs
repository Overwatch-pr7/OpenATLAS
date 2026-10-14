using System;
using Xunit;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Tests.Workbench
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.1 Unit Tests
    /// Verifies the WorkbenchViewModel state management, property changes,
    /// telemetry updates, delta time formatting, and commanding logic.
    /// </summary>
    public class WorkbenchViewModelTests
    {
        // Helper factory to construct a minimal dummy ReferenceLapIndex for isolated unit tests
        private static ReferenceLapIndex CreateMockReferenceLap()
        {
            const int length = 500;
            var distance = new int[length];
            var speed = new float[length];
            var throttle = new sbyte[length];
            var brake = new sbyte[length];
            var rpm = new int[length];
            var gear = new sbyte[length];
            var time = new float[length];

            for (int i = 0; i < length; i++)
            {
                distance[i] = i;
                speed[i] = 250f;
                throttle[i] = 100;
                brake[i] = 0;
                rpm[i] = 11000;
                gear[i] = 7;
                time[i] = i * 0.02f;
            }

            return new ReferenceLapIndex(distance, speed, throttle, brake, rpm, gear, time);
        }

        [Fact]
        public void ViewModel_InitializesWithExpectedDefaultState()
        {
            // Arrange & Act
            using var vm = new WorkbenchViewModel(referenceLap: null);

            // Assert
            Assert.Equal("STANDBY", vm.ConnectionStatus);
            Assert.False(vm.IsLiveConnected);
            Assert.False(vm.IsSimulating);
            Assert.Equal(0, vm.BufferCount);
            Assert.Equal(WorkbenchViewModel.DefaultBufferCapacity, vm.BufferCapacity);
            Assert.Equal(0f, vm.CurrentDistanceMeters);
            Assert.Equal(0f, vm.CurrentSimSpeedKmh);
            Assert.Equal(0f, vm.CurrentRefSpeedKmh);
            Assert.Equal(0f, vm.SpeedDeltaKmh);
            Assert.Equal(0f, vm.CurrentSimThrottlePercent);
            Assert.Equal(0f, vm.CurrentSimBrakePercent);
            Assert.Equal(0, vm.CurrentGear);
            Assert.Equal(0, vm.CurrentRpm);
            Assert.Equal("+0.000s", vm.DeltaDisplayString);
            Assert.Equal("LEVEL", vm.DeltaStatusString);
        }

        [Fact]
        public void InitializeReference_ConfiguresTrackAndLapMetadataCorrectly()
        {
            // Arrange
            var mockLap = CreateMockReferenceLap();
            using var vm = new WorkbenchViewModel(referenceLap: mockLap);

            // Act
            int trackLength = vm.ReferenceTrackLengthMeters;
            string lapTimeStr = vm.ReferenceLapTimeString;

            // Assert
            Assert.Equal(499, trackLength);
            Assert.NotNull(vm.ReferenceLap);
            Assert.NotNull(vm.DeltaEngine);
            Assert.False(string.IsNullOrWhiteSpace(lapTimeStr));
        }

        [Fact]
        public void UpdateSnapshot_UpdatesAllObservableTelemetryChannels()
        {
            // Arrange
            using var vm = new WorkbenchViewModel(referenceLap: null);

            var snapshot = new LiveDeltaSnapshot(
                distance: 650.5f,
                simSpeed: 312.4f,
                refSpeed: 320.0f,
                simThrottle: 85.0f,
                refThrottle: 100.0f,
                simBrake: 15.0f,
                refBrake: 0.0f,
                simGear: 7,
                refGear: 8,
                simRpm: 11450,
                refRpm: 12000,
                simTime: 12.450f,
                refTime: 12.265f,
                deltaTime: -0.185f
            );

            // Act
            vm.UpdateSnapshot(snapshot);

            // Assert
            Assert.Equal(650.5f, vm.CurrentDistanceMeters);
            Assert.Equal(312.4f, vm.CurrentSimSpeedKmh);
            Assert.Equal(320.0f, vm.CurrentRefSpeedKmh);
            Assert.Equal(-7.6f, vm.SpeedDeltaKmh, 0.01f);
            Assert.Equal(85.0f, vm.CurrentSimThrottlePercent);
            Assert.Equal(100.0f, vm.CurrentRefThrottlePercent);
            Assert.Equal(15.0f, vm.CurrentSimBrakePercent);
            Assert.Equal(0.0f, vm.CurrentRefBrakePercent);
            Assert.Equal(7, vm.CurrentGear);
            Assert.Equal(11450, vm.CurrentRpm);
            Assert.Equal(12.450f, vm.CurrentSimTimeSeconds);
            Assert.Equal(12.265f, vm.CurrentRefTimeSeconds);
            Assert.Equal(-0.185f, vm.CurrentDeltaSeconds);
        }

        [Fact]
        public void UpdateSnapshot_CorrectlyEvaluatesAheadState()
        {
            // Arrange
            using var vm = new WorkbenchViewModel(referenceLap: null);

            var aheadSnapshot = new LiveDeltaSnapshot(
                distance: 1200f,
                simSpeed: 330f,
                refSpeed: 325f,
                simThrottle: 100f,
                refThrottle: 100f,
                simBrake: 0f,
                refBrake: 0f,
                simGear: 8,
                refGear: 8,
                simRpm: 12500,
                refRpm: 12300,
                simTime: 20.000f,
                refTime: 20.420f,
                deltaTime: 0.420f // Ahead by 0.420s
            );

            // Act
            vm.UpdateSnapshot(aheadSnapshot);

            // Assert
            Assert.True(vm.IsAheadOfReference);
            Assert.Equal("+0.420s", vm.DeltaDisplayString);
            Assert.Equal("FASTER", vm.DeltaStatusString);
        }

        [Fact]
        public void UpdateSnapshot_CorrectlyEvaluatesBehindState()
        {
            // Arrange
            using var vm = new WorkbenchViewModel(referenceLap: null);

            var behindSnapshot = new LiveDeltaSnapshot(
                distance: 1200f,
                simSpeed: 310f,
                refSpeed: 325f,
                simThrottle: 90f,
                refThrottle: 100f,
                simBrake: 10f,
                refBrake: 0f,
                simGear: 7,
                refGear: 8,
                simRpm: 11500,
                refRpm: 12300,
                simTime: 20.500f,
                refTime: 20.000f,
                deltaTime: -0.500f // Behind by 0.500s
            );

            // Act
            vm.UpdateSnapshot(behindSnapshot);

            // Assert
            Assert.False(vm.IsAheadOfReference);
            Assert.Equal("-0.500s", vm.DeltaDisplayString);
            Assert.Equal("SLOWER", vm.DeltaStatusString);
        }

        [Fact]
        public void ResetLap_ClearsTelemetryAndResetsDeltaCounters()
        {
            // Arrange
            using var vm = new WorkbenchViewModel(referenceLap: null);

            var activeSnapshot = new LiveDeltaSnapshot(
                distance: 2500f,
                simSpeed: 280f,
                refSpeed: 275f,
                simThrottle: 100f,
                refThrottle: 100f,
                simBrake: 0f,
                refBrake: 0f,
                simGear: 6,
                refGear: 6,
                simRpm: 11000,
                refRpm: 10800,
                simTime: 45.120f,
                refTime: 45.450f,
                deltaTime: 0.330f
            );
            vm.UpdateSnapshot(activeSnapshot);

            // Act
            vm.ResetLap();

            // Assert
            Assert.Equal(0f, vm.CurrentDistanceMeters);
            Assert.Equal(0f, vm.CurrentSimSpeedKmh);
            Assert.Equal(0f, vm.CurrentRefSpeedKmh);
            Assert.Equal(0f, vm.SpeedDeltaKmh);
            Assert.Equal(0f, vm.CurrentSimThrottlePercent);
            Assert.Equal(0f, vm.CurrentSimBrakePercent);
            Assert.Equal(0, vm.CurrentGear);
            Assert.Equal(0, vm.CurrentRpm);
            Assert.Equal(0f, vm.CurrentSimTimeSeconds);
            Assert.Equal(0f, vm.CurrentRefTimeSeconds);
            Assert.Equal(0f, vm.CurrentDeltaSeconds);
            Assert.Equal("+0.000s", vm.DeltaDisplayString);
            Assert.Equal("LEVEL", vm.DeltaStatusString);
            Assert.Equal(0, vm.BufferCount);
        }

        [Fact]
        public void RelayCommand_ExecutesActionAndEvaluatesCanExecute()
        {
            // Arrange
            bool executed = false;
            bool allowExecution = false;

            var command = new RelayCommand(
                execute: () => executed = true,
                canExecute: () => allowExecution
            );

            // Act & Assert
            Assert.False(command.CanExecute(null));
            command.Execute(null);
            Assert.False(executed); // CanExecute was false

            allowExecution = true;
            Assert.True(command.CanExecute(null));
            command.Execute(null);
            Assert.True(executed);
        }
    }
}
