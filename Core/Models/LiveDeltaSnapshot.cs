using System.Runtime.InteropServices;

namespace OpenATLAS.Core.Models
{
    // Task 3.3: Live Telemetry Delta Snapshot
    // Sequential value type for zero GC allocations across calculation and UI pipelines.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct LiveDeltaSnapshot
    {
        public readonly float Distance;
        public readonly float SimSpeed;
        public readonly float RefSpeed;
        public readonly float SimThrottle;
        public readonly float RefThrottle;
        public readonly float SimBrake;
        public readonly float RefBrake;
        public readonly int SimGear;
        public readonly int RefGear;
        public readonly int SimRpm;
        public readonly int RefRpm;
        public readonly float SimTime;
        public readonly float RefTime;
        public readonly float DeltaTime;

        public LiveDeltaSnapshot(
            float distance,
            float simSpeed,
            float refSpeed,
            float simThrottle,
            float refThrottle,
            float simBrake,
            float refBrake,
            int simGear,
            int refGear,
            int simRpm,
            int refRpm,
            float simTime,
            float refTime,
            float deltaTime)
        {
            Distance = distance;
            SimSpeed = simSpeed;
            RefSpeed = refSpeed;
            SimThrottle = simThrottle;
            RefThrottle = refThrottle;
            SimBrake = simBrake;
            RefBrake = refBrake;
            SimGear = simGear;
            RefGear = refGear;
            SimRpm = simRpm;
            RefRpm = refRpm;
            SimTime = simTime;
            RefTime = refTime;
            DeltaTime = deltaTime;
        }

        // Positive delta indicates sim driver is ahead of reference lap; negative indicates behind.
        public bool IsAhead => DeltaTime > 0f;
    }
}
