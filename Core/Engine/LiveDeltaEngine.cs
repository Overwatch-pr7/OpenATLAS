using System;
using OpenATLAS.Core.Collections;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Core.Engine
{
    // Task 3.3: The Delta Algorithm & Live Calculation Engine
    // Continuously compares live simulator telemetry against the distance-indexed reference lap.
    // Computes: DeltaTime = t_ref(s) - t_sim(s)
    // Positive delta indicates sim car is ahead of reference; negative indicates behind.
    public class LiveDeltaEngine
    {
        private readonly object _stateLock = new();
        private ReferenceLapIndex _referenceLap;
        private float _lastDistance = -1f;

        public ReferenceLapIndex ReferenceLap
        {
            get => _referenceLap;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _referenceLap = value;
            }
        }

        // Optional circular buffer for storing continuous delta snapshots for 60Hz UI rendering
        public CircularBuffer<LiveDeltaSnapshot>? OutputBuffer { get; set; }

        // Event fired whenever a new delta snapshot is computed
        public event Action<LiveDeltaSnapshot>? OnDeltaCalculated;

        // Event fired when the car crosses the start/finish line (lap reset detected)
        public event Action? OnLapCompleted;

        public LiveDeltaEngine(ReferenceLapIndex referenceLap, CircularBuffer<LiveDeltaSnapshot>? outputBuffer = null)
        {
            ArgumentNullException.ThrowIfNull(referenceLap);
            _referenceLap = referenceLap;
            OutputBuffer = outputBuffer;
        }

        // Core Delta Algorithm: Pure functional calculation for sub-millisecond distance alignment
        // simThrottle / simBrake are 0-100 %, like the reference laps. AC's raw 0-1 pedal values are
        // scaled by the Process overloads below. (Guessing the scale from the value would turn a
        // genuine 1 % in a saved lap into 100 %.)
        public LiveDeltaSnapshot Calculate(
            float simDistance,
            float simTime,
            float simSpeed,
            float simThrottle,
            float simBrake,
            int simGear,
            int simRpm)
        {
            // Fetch reference sample at the exact simulated track distance (sub-meter interpolation)
            ReferenceSample refSample = _referenceLap.GetSampleAtDistance(simDistance, interpolate: true);

            // Compute delta: DeltaTime = t_ref(s) - t_sim(s)
            // If refTime > simTime, sim took LESS time to reach distance 's', so delta is positive (ahead).
            // If refTime < simTime, sim took MORE time to reach distance 's', so delta is negative (behind).
            float deltaTime = refSample.TimeSeconds - simTime;

            return new LiveDeltaSnapshot(
                distance: simDistance,
                simSpeed: simSpeed,
                refSpeed: refSample.SpeedKmh,
                simThrottle: simThrottle,
                refThrottle: refSample.Throttle,
                simBrake: simBrake,
                refBrake: refSample.Brake,
                simGear: simGear,
                refGear: refSample.Gear,
                simRpm: simRpm,
                refRpm: refSample.Rpms,
                simTime: simTime,
                refTime: refSample.TimeSeconds,
                deltaTime: deltaTime
            );
        }

        // Ingestion method for TelemetrySnapshot
        public LiveDeltaSnapshot Process(in TelemetrySnapshot snapshot)
        {
            CheckLapCrossover(snapshot.DistanceMeters);

            LiveDeltaSnapshot deltaSnapshot = Calculate(
                simDistance: snapshot.DistanceMeters,
                simTime: (float)snapshot.TimestampSeconds,
                simSpeed: snapshot.SpeedKmh,
                simThrottle: snapshot.Throttle * 100f, // 0-1 -> %
                simBrake: snapshot.Brake * 100f,
                simGear: snapshot.Gear,
                simRpm: snapshot.Rpms
            );

            OutputBuffer?.Push(deltaSnapshot);
            OnDeltaCalculated?.Invoke(deltaSnapshot);

            return deltaSnapshot;
        }

        // Ingestion method for raw AcPhysicsPacket
        public LiveDeltaSnapshot Process(in AcPhysicsPacket packet, float simDistance, float simElapsedSeconds)
        {
            CheckLapCrossover(simDistance);

            LiveDeltaSnapshot deltaSnapshot = Calculate(
                simDistance: simDistance,
                simTime: simElapsedSeconds,
                simSpeed: packet.SpeedKmh,
                simThrottle: packet.Gas * 100f, // 0-1 -> %
                simBrake: packet.Brake * 100f,
                simGear: packet.DisplayGear, // AC counts R=0, N=1; convert to N=0, 1st=1
                simRpm: packet.Rpms
            );

            OutputBuffer?.Push(deltaSnapshot);
            OnDeltaCalculated?.Invoke(deltaSnapshot);

            return deltaSnapshot;
        }

        // Detects lap crossover when the distance wraps around back near 0
        private void CheckLapCrossover(float currentDistance)
        {
            lock (_stateLock)
            {
                if (_lastDistance > 100f && currentDistance < 50f)
                {
                    OnLapCompleted?.Invoke();
                }
                _lastDistance = currentDistance;
            }
        }

        // Resets engine tracking state
        public void Reset()
        {
            lock (_stateLock)
            {
                _lastDistance = -1f;
            }
        }
    }
}
