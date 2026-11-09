using System;
using System.Collections.Generic;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Core.Import
{
    // One time-based telemetry sample of a user lap, as read from a CSV log or recorded live from AC.
    // DistanceMeters is NaN when the source has no distance channel (it is then integrated from speed).
    // Throttle/brake are 0-100 %.
    public readonly record struct RawLapSample(
        double TimeSeconds,
        double DistanceMeters,
        double SpeedKmh,
        double ThrottlePercent,
        double BrakePercent,
        int Gear,
        int Rpm);

    // Lap Normalizer
    // C# port of extract_reference.py's pipeline (Tasks 1.2/1.3), so user laps end up in exactly the
    // same shape as the F1 reference laps: time and distance zeroed at the start of the lap and one
    // sample per metre. Continuous channels are interpolated linearly; brake uses the nearest sample
    // and gear holds its previous value, as in the Python resampler.
    public static class LapNormalizer
    {
        // Shorter than any real circuit: such "laps" are partial (out/in laps, logging gaps)
        public const double MinLapLengthMeters = 1000;

        public static ReferenceLapIndex Normalize(IReadOnlyList<RawLapSample> samples)
        {
            ArgumentNullException.ThrowIfNull(samples);
            if (samples.Count < 10)
            {
                throw new ArgumentException($"A lap needs at least 10 samples, got {samples.Count}.", nameof(samples));
            }

            int n = samples.Count;
            var time = new double[n];
            var distance = new double[n];
            bool hasDistance = !double.IsNaN(samples[0].DistanceMeters);

            for (int i = 0; i < n; i++)
            {
                time[i] = samples[i].TimeSeconds - samples[0].TimeSeconds;

                if (hasDistance)
                {
                    distance[i] = samples[i].DistanceMeters - samples[0].DistanceMeters;
                }
                else if (i > 0)
                {
                    // Trapezoid rule, km/h -> m/s (same as FastF1's add_distance)
                    double dt = time[i] - time[i - 1];
                    distance[i] = distance[i - 1] + dt * (samples[i].SpeedKmh + samples[i - 1].SpeedKmh) / 2.0 / 3.6;
                }
            }

            // Interpolation needs strictly increasing distance: drop samples where the car did not move
            var keep = new List<int>(n) { 0 };
            for (int i = 1; i < n; i++)
            {
                if (distance[i] > distance[keep[^1]]) keep.Add(i);
            }

            double lapLength = distance[keep[^1]];
            if (lapLength < MinLapLengthMeters)
            {
                throw new ArgumentException($"Lap is only {lapLength:F0} m long; it is probably incomplete.", nameof(samples));
            }

            // Tolerance so float accumulation (2999.9999 m) doesn't lose the last metre
            int length = (int)Math.Floor(lapLength + 1e-6);
            var outDistance = new int[length + 1];
            var outSpeed = new float[length + 1];
            var outThrottle = new sbyte[length + 1];
            var outBrake = new sbyte[length + 1];
            var outRpm = new int[length + 1];
            var outGear = new sbyte[length + 1];
            var outTime = new float[length + 1];

            int k = 0; // index into keep: the kept sample at or before the current metre
            for (int metre = 0; metre <= length; metre++)
            {
                while (k < keep.Count - 2 && distance[keep[k + 1]] <= metre) k++;

                int a = keep[k], b = keep[Math.Min(k + 1, keep.Count - 1)];
                double span = distance[b] - distance[a];
                double t = span > 0 ? Math.Clamp((metre - distance[a]) / span, 0, 1) : 0;

                outDistance[metre] = metre;
                outTime[metre] = (float)Lerp(time[a], time[b], t);
                outSpeed[metre] = (float)Lerp(samples[a].SpeedKmh, samples[b].SpeedKmh, t);
                outThrottle[metre] = ToPercent(Lerp(samples[a].ThrottlePercent, samples[b].ThrottlePercent, t));
                outRpm[metre] = (int)Math.Round(Lerp(samples[a].Rpm, samples[b].Rpm, t));
                outBrake[metre] = ToPercent(t < 0.5 ? samples[a].BrakePercent : samples[b].BrakePercent); // nearest
                outGear[metre] = (sbyte)Math.Clamp(samples[a].Gear, sbyte.MinValue, sbyte.MaxValue);       // previous
            }

            return new ReferenceLapIndex(outDistance, outSpeed, outThrottle, outBrake, outRpm, outGear, outTime);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static sbyte ToPercent(double value) => (sbyte)Math.Clamp(Math.Round(value), 0, 100);
    }
}
