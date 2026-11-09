using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenATLAS.Core.Reference
{
    // Track Layout (track.json)
    // Sector boundaries and corner positions for one Grand Prix, written by extract_reference.py
    // next to the reference laps: references/<Year>/<Grand Prix>/track.json
    // All distances are metres from the start/finish line, on the same axis as the reference laps.
    public class TrackLayout
    {
        public const string FileName = "track.json";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public string Event { get; init; } = "";
        public string Location { get; init; } = "";
        public float LengthMeters { get; init; }
        public IReadOnlyList<TrackSector> Sectors { get; init; } = [];
        public IReadOnlyList<TrackCorner> Corners { get; init; } = [];

        // Track map: [distance_m, x_m, y_m] every few metres along the lap, already rotated to
        // the official F1 map orientation. Empty for layouts written before the map existed.
        public float[][] Outline { get; init; } = [];
        public float MapRotationDegrees { get; init; }

        public bool HasOutline => Outline.Length >= 2;

        // Distance covered by the outline (the lap it was recorded on)
        public float OutlineLengthMeters => HasOutline ? Outline[^1][0] : 0f;

        // Map height / width, used to size the map panel for this track
        public double MapAspectRatio
        {
            get
            {
                if (!HasOutline) return 1.0;
                double width = Outline.Max(p => p[1]) - Outline.Min(p => p[1]);
                double height = Outline.Max(p => p[2]) - Outline.Min(p => p[2]);
                return width > 0 ? height / width : 1.0;
            }
        }

        // Map position at a fraction of the lap (0 = start/finish line, 1 = back at the line).
        // Fractions rather than metres, so laps of slightly different length (or an AC track)
        // still land on the right part of the circuit.
        public (double X, double Y) PointAtFraction(double fraction)
        {
            if (!HasOutline) return (0, 0);

            fraction -= Math.Floor(fraction); // wrap into [0, 1)
            double target = fraction * OutlineLengthMeters;

            // Points are evenly spaced, but a binary search keeps this correct for any spacing
            int lo = 0, hi = Outline.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (Outline[mid][0] <= target) lo = mid; else hi = mid;
            }

            float[] a = Outline[lo], b = Outline[hi];
            double span = b[0] - a[0];
            double t = span > 0 ? (target - a[0]) / span : 0;
            return (a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t);
        }

        // Corners whose position lies inside the sector, in lap order.
        public IReadOnlyList<TrackCorner> CornersIn(TrackSector sector)
        {
            return Corners
                .Where(c => c.DistanceMeters >= sector.StartMeters && c.DistanceMeters < sector.EndMeters)
                .OrderBy(c => c.DistanceMeters)
                .ToList();
        }

        public static TrackLayout Load(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var layout = JsonSerializer.Deserialize<TrackLayout>(stream, JsonOptions)
                ?? throw new InvalidDataException($"Empty track layout: {filePath}");

            if (layout.Sectors.Count == 0)
            {
                throw new InvalidDataException($"Track layout has no sectors: {filePath}");
            }

            return layout;
        }
    }

    public record TrackSector(int Number, float StartMeters, float EndMeters);

    // Letter distinguishes split corners (e.g. 'a'/'b'); Name is optional and filled in by hand.
    // LabelX/LabelY: where the turn number goes on the track map (absent in older track.json files).
    public record TrackCorner(int Number, string Letter, float DistanceMeters, string Name,
                              float? LabelX = null, float? LabelY = null)
    {
        public string Label => $"T{Number}{Letter}";
    }
}
