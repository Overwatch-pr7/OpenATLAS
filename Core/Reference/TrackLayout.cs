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
    public record TrackCorner(int Number, string Letter, float DistanceMeters, string Name)
    {
        public string Label => $"T{Number}{Letter}";
    }
}
