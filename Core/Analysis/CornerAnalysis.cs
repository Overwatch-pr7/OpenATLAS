using System;
using System.Collections.Generic;
using System.Linq;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Core.Analysis
{
    // Task 5.2: Corner Analysis
    // Splits a lap into one zone per corner (from track.json) and measures, per zone, the minimum
    // speed, where the driver started braking and where they got back on the throttle. Two laps
    // measured over the same zones can then be compared corner by corner.

    /// <summary>
    /// A lap as plain distance-ordered channels. Built from a saved/reference lap or from the live
    /// trace of the lap in progress (which may stop partway round).
    /// </summary>
    public sealed class LapTrace
    {
        public double[] Distance { get; }
        public double[] Speed { get; }
        public double[] Throttle { get; } // 0-100 %
        public double[] Brake { get; }    // 0-100 %
        public double[] Time { get; }     // lap time, s

        /// <summary>Full lap length in metres (for a partial live lap: the reference lap length).</summary>
        public double LengthMeters { get; }

        public int Count => Distance.Length;
        public double CoveredMeters => Count > 0 ? Distance[^1] : 0;

        public LapTrace(double[] distance, double[] speed, double[] throttle, double[] brake, double[] time, double lengthMeters)
        {
            int n = distance.Length;
            if (speed.Length != n || throttle.Length != n || brake.Length != n || time.Length != n)
            {
                throw new ArgumentException("All lap channels must have the same length.");
            }

            Distance = distance;
            Speed = speed;
            Throttle = throttle;
            Brake = brake;
            Time = time;
            LengthMeters = lengthMeters;
        }

        public static LapTrace FromLap(ReferenceLapIndex lap)
        {
            ArgumentNullException.ThrowIfNull(lap);
            int n = lap.SampleCount;
            var d = new double[n];
            var s = new double[n];
            var t = new double[n];
            var b = new double[n];
            var time = new double[n];
            for (int i = 0; i < n; i++)
            {
                d[i] = lap.Distance[i];
                s[i] = lap.Speed[i];
                t[i] = lap.Throttle[i];
                b[i] = lap.Brake[i];
                time[i] = lap.Time[i];
            }
            return new LapTrace(d, s, t, b, time, lap.TrackLengthMeters);
        }

        /// <summary>First sample at or after the distance (Count when past the end).</summary>
        public int IndexAtOrAfter(double distance)
        {
            int index = Array.BinarySearch(Distance, distance);
            return index >= 0 ? index : ~index;
        }

        /// <summary>Lap time at a distance, interpolated. Null outside the covered range.</summary>
        public double? TimeAt(double distance)
        {
            if (Count == 0 || distance < Distance[0] || distance > Distance[^1]) return null;

            int next = IndexAtOrAfter(distance);
            if (next < Count && Distance[next] == distance) return Time[next];

            int prev = next - 1;
            double span = Distance[next] - Distance[prev];
            double f = span > 0 ? (distance - Distance[prev]) / span : 0;
            return Time[prev] + f * (Time[next] - Time[prev]);
        }
    }

    /// <summary>
    /// The stretch of track measured for one corner, in metres on the reference lap.
    /// Starts halfway from the previous corner and ends halfway to the next one, so the braking
    /// zone and the exit both fall inside it; capped so a corner after a long straight does not
    /// reach back into the straight.
    /// </summary>
    public sealed record CornerZone(TrackCorner Corner, double StartMeters, double ApexMeters, double EndMeters)
    {
        public string Label => Corner.Label;
    }

    /// <summary>
    /// Measured values for one corner on one lap, in reference-lap metres. A null value means
    /// the lap has not reached that part of the corner yet, or (brake / throttle) did not happen.
    /// </summary>
    public sealed record CornerMetrics(
        double? ZoneSeconds,
        double? MinSpeedKmh,
        double? MinSpeedAtMeters,
        double? BrakePointMeters,
        double? ThrottlePickUpMeters,
        bool IsFlatOut)
    {
        public static readonly CornerMetrics NotReached = new(null, null, null, null, null, false);
        public bool HasData => MinSpeedKmh.HasValue;
    }

    public static class CornerAnalyzer
    {
        /// <summary>Brake at or above this counts as braking (F1 data is 0 or 100 %).</summary>
        public const double BrakeThresholdPercent = 10;

        /// <summary>Throttle pick-up: where the throttle passes this on the way out of the corner.</summary>
        public const double ThrottleThresholdPercent = 50;

        public const double MaxApproachMeters = 600;
        public const double MaxExitMeters = 400;

        // How far back a braking zone that started before the corner zone is followed
        private const double MaxBrakeLookBackMeters = 400;

        public static IReadOnlyList<CornerZone> BuildZones(IReadOnlyList<TrackCorner> corners, double lapLengthMeters)
        {
            var ordered = corners.OrderBy(c => c.DistanceMeters).ToList();
            var zones = new List<CornerZone>(ordered.Count);

            for (int i = 0; i < ordered.Count; i++)
            {
                double apex = ordered[i].DistanceMeters;
                double start = i > 0 ? (ordered[i - 1].DistanceMeters + apex) / 2 : 0;
                double end = i < ordered.Count - 1 ? (apex + ordered[i + 1].DistanceMeters) / 2 : lapLengthMeters;

                start = Math.Max(start, apex - MaxApproachMeters);
                end = Math.Min(end, apex + MaxExitMeters);
                zones.Add(new CornerZone(ordered[i], Math.Max(0, start), apex, Math.Min(lapLengthMeters, end)));
            }

            return zones;
        }

        /// <summary>
        /// Measures one corner on a lap. Zone metres are on the reference lap; <paramref name="scale"/>
        /// maps them onto this lap (its length / reference length), and results are mapped back,
        /// so a lap on a slightly longer AC track still compares corner for corner.
        /// </summary>
        public static CornerMetrics Measure(LapTrace lap, CornerZone zone, double scale = 1.0)
        {
            ArgumentNullException.ThrowIfNull(lap);
            if (scale <= 0) scale = 1.0;

            double start = zone.StartMeters * scale;
            double end = zone.EndMeters * scale;

            int first = lap.IndexAtOrAfter(start);
            if (lap.Count == 0 || first >= lap.Count || lap.Distance[0] > start + 5 || lap.CoveredMeters < zone.ApexMeters * scale)
            {
                return CornerMetrics.NotReached; // not driven up to the corner yet (or joined mid-corner)
            }

            bool complete = lap.CoveredMeters >= end;
            int last = Math.Min(lap.IndexAtOrAfter(end), lap.Count - 1);
            if (lap.Distance[last] > end && last > first) last--;

            // Minimum speed through the zone
            int minIndex = first;
            for (int i = first + 1; i <= last; i++)
            {
                if (lap.Speed[i] < lap.Speed[minIndex]) minIndex = i;
            }

            // Braking point: first brake application before the slowest point. If the driver is
            // already braking when the zone starts (a chicane), follow it back to where it began.
            int? brakeIndex = null;
            for (int i = first; i <= minIndex; i++)
            {
                if (lap.Brake[i] >= BrakeThresholdPercent) { brakeIndex = i; break; }
            }
            if (brakeIndex == first)
            {
                int i = first;
                while (i > 0 && lap.Brake[i - 1] >= BrakeThresholdPercent && lap.Distance[i - 1] >= start - MaxBrakeLookBackMeters) i--;
                brakeIndex = i;
            }

            // Throttle pick-up: start of the throttle-on run that contains or follows the slowest point
            int? pickUpIndex = null;
            bool flat = false;
            if (lap.Throttle[minIndex] >= ThrottleThresholdPercent)
            {
                int i = minIndex;
                int floor = brakeIndex ?? first;
                while (i > floor && lap.Throttle[i - 1] >= ThrottleThresholdPercent) i--;
                if (i == first && brakeIndex == null) flat = true; // never lifted below the threshold
                else pickUpIndex = i;
            }
            else
            {
                for (int i = minIndex + 1; i < lap.Count && lap.Distance[i] <= end + MaxExitMeters * scale; i++)
                {
                    if (lap.Throttle[i] >= ThrottleThresholdPercent) { pickUpIndex = i; break; }
                }
            }

            double? zoneSeconds = complete ? lap.TimeAt(end) - lap.TimeAt(start) : null;

            return new CornerMetrics(
                zoneSeconds,
                lap.Speed[minIndex],
                lap.Distance[minIndex] / scale,
                brakeIndex.HasValue ? lap.Distance[brakeIndex.Value] / scale : null,
                pickUpIndex.HasValue ? lap.Distance[pickUpIndex.Value] / scale : null,
                flat);
        }

        public static IReadOnlyList<CornerMetrics> MeasureAll(LapTrace lap, IReadOnlyList<CornerZone> zones, double scale = 1.0)
        {
            return zones.Select(z => Measure(lap, z, scale)).ToList();
        }
    }
}
