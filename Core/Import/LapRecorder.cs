using System.Collections.Generic;

namespace OpenATLAS.Core.Import
{
    // Lap Recorder
    // Collects live samples while connected to AC. When AC reports a completed lap the caller
    // calls FinishLap(): a full lap is handed back for normalizing and saving, anything partial
    // (joined mid-lap, came out of the pits, reset to the pits) is thrown away.
    public sealed class LapRecorder
    {
        // A recorded lap must start this close to the line: by distance when samples have one,
        // else by lap time (AC's lap clock restarts at 0 on the line)...
        public const double MaxStartDistanceMeters = 50;
        public const double MaxStartTimeSeconds = 1.0;

        // ...and cover at least this share of the reference lap length
        public const double MinLapShare = 0.9;

        private readonly List<RawLapSample> _samples = [];

        public int SampleCount => _samples.Count;

        public void Add(in RawLapSample sample) => _samples.Add(sample);

        /// <summary>
        /// Ends the current lap. Returns its samples when it was a complete lap, else null.
        /// Recording continues into the next lap either way.
        /// </summary>
        public IReadOnlyList<RawLapSample>? FinishLap(double referenceLengthMeters) => FinishLap(referenceLengthMeters, out _);

        /// <param name="rejectReason">Why the lap was not kept (for the status bar), or null when it was.</param>
        public IReadOnlyList<RawLapSample>? FinishLap(double referenceLengthMeters, out string? rejectReason)
        {
            List<RawLapSample>? lap = null;
            double covered = _samples.Count >= 2 ? CoveredMeters() : 0;

            if (_samples.Count < 10)
            {
                rejectReason = "too few samples were recorded";
            }
            else if (!StartedAtTheLine())
            {
                rejectReason = "recording started partway round the lap";
            }
            else if (covered < referenceLengthMeters * MinLapShare)
            {
                rejectReason = $"only {covered:N0} m of {referenceLengthMeters:N0} m were driven (pit lane or reset?)";
            }
            else
            {
                rejectReason = null;
                lap = [.. _samples];
            }

            _samples.Clear();
            return lap;
        }

        public void Discard() => _samples.Clear();

        private bool StartedAtTheLine() => double.IsNaN(_samples[0].DistanceMeters)
            ? _samples[0].TimeSeconds <= MaxStartTimeSeconds
            : _samples[0].DistanceMeters <= MaxStartDistanceMeters;

        private double CoveredMeters()
        {
            if (!double.IsNaN(_samples[0].DistanceMeters)) return _samples[^1].DistanceMeters - _samples[0].DistanceMeters;

            double distance = 0;
            for (int i = 1; i < _samples.Count; i++)
            {
                double dt = _samples[i].TimeSeconds - _samples[i - 1].TimeSeconds;
                if (dt > 0) distance += dt * (_samples[i].SpeedKmh + _samples[i - 1].SpeedKmh) / 2 / 3.6;
            }
            return distance;
        }
    }
}
