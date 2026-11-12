using System;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Core.Engine
{
    public enum AcLapState
    {
        /// <summary>Not started, or AC is in a menu, replay or paused: samples are ignored.</summary>
        Paused,

        /// <summary>The current lap did not start at the line (out lap, joined mid-lap, back from the pits).</summary>
        WaitingForLine,

        /// <summary>A lap that started at the line is being timed.</summary>
        Timing
    }

    /// <summary>Where the car is on the lap being timed. Only meaningful when <see cref="IsTimed"/> is true.</summary>
    public readonly record struct AcLapPosition(bool IsTimed, float DistanceMeters, float LapTimeSeconds, bool NewLap);

    /// <summary>
    /// Live lap position from Assetto Corsa's own timing instead of integrating speed:
    /// distance = NormalizedCarPosition x reference lap length (so an AC track of slightly different
    /// length lines up with the reference corner for corner, and nothing drifts), lap time = AC's lap
    /// clock (stops while paused), and a new lap starts when AC's lap counter changes.
    /// A lap is only compared once it has started at the start/finish line, so out laps, laps joined
    /// partway round and laps after returning to the pits never produce a meaningless delta.
    /// </summary>
    public sealed class AcLapTracker
    {
        // Joining this close to the line, this early in the lap, still counts as a lap from the line
        private const float JoinPositionWindow = 0.02f;
        private const float JoinTimeWindowSeconds = 3f;

        // AC's position and lap counter do not flip on exactly the same sample at the line
        private const float LineFractionWindow = 0.1f;
        private const float LineTimeWindowSeconds = 10f;

        // A backward jump bigger than this (without a new lap) is a teleport, e.g. back to the pits
        private const float TeleportFraction = 0.05f;

        private readonly float _lengthMeters;
        private int _lastCompletedLaps = -1;
        private float _lastFraction = -1f;

        public AcLapState State { get; private set; } = AcLapState.Paused;

        public AcLapTracker(float referenceLengthMeters)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(referenceLengthMeters, 0f);
            _lengthMeters = referenceLengthMeters;
        }

        public AcLapPosition Update(in AcGraphicsPacket graphics) =>
            Update(graphics.Status, graphics.CompletedLaps, graphics.NormalizedCarPosition, graphics.CurrentTimeMs, graphics.IsInPit != 0);

        public AcLapPosition Update(AcStatus status, int completedLaps, float normalizedPosition, int currentTimeMs, bool isInPitBox)
        {
            if (status != AcStatus.Live)
            {
                if (State == AcLapState.Timing) State = AcLapState.Paused; // resumes timing when Live again
                return default;
            }

            float fraction = Math.Clamp(normalizedPosition, 0f, 1f);
            float lapTime = Math.Max(0, currentTimeMs) / 1000f;

            bool first = _lastCompletedLaps < 0;
            bool newLap = !first && completedLaps != _lastCompletedLaps;
            bool teleported = !first && !newLap && _lastFraction >= 0 && fraction < _lastFraction - TeleportFraction
                              && !(lapTime > LineTimeWindowSeconds && fraction < LineFractionWindow); // not the line wrap
            _lastCompletedLaps = completedLaps;
            _lastFraction = fraction;

            if (isInPitBox || teleported)
            {
                State = AcLapState.WaitingForLine;
            }
            else if (newLap)
            {
                State = AcLapState.Timing;
            }
            else if (first)
            {
                State = fraction < JoinPositionWindow && lapTime < JoinTimeWindowSeconds
                    ? AcLapState.Timing
                    : AcLapState.WaitingForLine;
            }
            else if (State == AcLapState.Paused)
            {
                State = AcLapState.Timing; // back from pause on a lap that was already being timed
            }

            if (State != AcLapState.Timing) return default;

            // Around the line: just after a new lap the position can still read ~1.0, and just before
            // the lap counter changes it can already read ~0.0
            if (fraction > 1f - LineFractionWindow && lapTime < LineTimeWindowSeconds) fraction = 0f;
            else if (fraction < LineFractionWindow && lapTime > LineTimeWindowSeconds) fraction = 1f;

            return new AcLapPosition(true, fraction * _lengthMeters, lapTime, newLap);
        }
    }
}
