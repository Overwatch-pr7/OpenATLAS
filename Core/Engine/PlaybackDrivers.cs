using System;
using OpenATLAS.Core.Models;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Core.Engine
{
    // One step of a played-back lap. Distance is on the reference lap's scale (metres);
    // NewLap is true on the step where the lap wraps back to the line.
    public readonly record struct PlaybackSample(
        float Distance, float Time, float Speed, float Throttle, float Brake, int Gear, int Rpm, bool NewLap);

    // A Racer 1 that is not live AC: produces one sample per fixed time step.
    public interface IPlaybackDriver
    {
        PlaybackSample Next(float dt);
    }

    // SIMULATION: a synthetic driver built from the reference lap itself, 1.5 % slower than the
    // reference for the first 2,000 m and 1 % faster after that, using the reference's own inputs.
    public sealed class SyntheticDriver : IPlaybackDriver
    {
        private readonly ReferenceLapIndex _reference;
        private float _distance;
        private float _time;

        public SyntheticDriver(ReferenceLapIndex reference)
        {
            _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        }

        public PlaybackSample Next(float dt)
        {
            ReferenceSample refSample = _reference.GetSampleAtDistance(_distance);

            float variation = _distance < 2000f ? 0.985f : 1.01f;
            float speed = refSample.SpeedKmh * variation;

            _distance += speed / 3.6f * dt;
            _time += dt;

            bool newLap = _distance >= _reference.TrackLengthMeters;
            if (newLap)
            {
                _distance = 0f;
                _time = 0f;
            }

            return new PlaybackSample(_distance, _time, speed, refSample.Throttle, refSample.Brake, refSample.Gear, refSample.Rpms, newLap);
        }
    }

    // SAVED LAP: replays one of the user's laps in real time. Its distance is mapped onto the
    // reference by fraction of the lap, so a lap from an AC track that is slightly longer or shorter
    // than the F1 reference still lines up corner for corner.
    public sealed class SavedLapDriver : IPlaybackDriver
    {
        private readonly ReferenceLapIndex _lap;
        private readonly float _referenceLength;
        private float _time;

        public SavedLapDriver(ReferenceLapIndex lap, float referenceLengthMeters)
        {
            _lap = lap ?? throw new ArgumentNullException(nameof(lap));
            _referenceLength = referenceLengthMeters;
        }

        public PlaybackSample Next(float dt)
        {
            _time += dt;
            bool newLap = _time >= _lap.TotalLapTimeSeconds;
            if (newLap) _time = 0f;

            float lapDistance = _lap.GetDistanceAtTime(_time);
            ReferenceSample sample = _lap.GetSampleAtDistance(lapDistance);
            float distance = _lap.TrackLengthMeters > 0 ? lapDistance / _lap.TrackLengthMeters * _referenceLength : 0f;

            return new PlaybackSample(distance, _time, sample.SpeedKmh, sample.Throttle, sample.Brake, sample.Gear, sample.Rpms, newLap);
        }
    }
}
