using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenATLAS.Core.Reference
{
    // Reference Lap Library
    // Indexes the reference laps exported by extract_reference.py, laid out on disk as:
    //   references/<Year>/<Track>/<DRIVER>.parquet
    // The folder structure is the catalog, so adding a lap only requires dropping in a file.
    // The user's own laps (imported or recorded) live alongside as a pseudo-year:
    //   references/My Laps/<Grand Prix>/<lap name>.parquet
    public class ReferenceLibrary
    {
        public const string DefaultFolderName = "references";
        public const string UserLapsFolderName = "My Laps";
        private const string FileExtension = ".parquet";

        public string RootPath { get; }

        public ReferenceLibrary(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            RootPath = rootPath;
        }

        // Years that contain at least one track folder: seasons newest first, then "My Laps".
        public IReadOnlyList<string> GetYears()
        {
            return ListDirectories(RootPath)
                .Where(year => GetTracks(year).Count > 0)
                .OrderBy(year => IsSeason(year) ? 0 : 1)
                .ThenByDescending(year => year, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Every Grand Prix that has F1 reference data in any season, alphabetical.
        // These are the tracks a user lap can be filed under.
        public IReadOnlyList<string> GetAllGrandPrix()
        {
            return ListDirectories(RootPath)
                .Where(IsSeason)
                .SelectMany(GetTracks)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The user's saved laps for a Grand Prix, alphabetical.
        public IReadOnlyList<string> GetUserLaps(string grandPrix) => GetDrivers(UserLapsFolderName, grandPrix);

        /// <summary>
        /// Saves a user lap to My Laps/&lt;Grand Prix&gt;/&lt;name&gt;.parquet and returns the name it was saved
        /// under (made unique if taken). The Grand Prix's track.json is copied in from the newest season
        /// that has one, so sectors and the track map also work when the lap is used as Racer 2.
        /// </summary>
        public string SaveUserLap(ReferenceLapIndex lap, string grandPrix, string lapName)
        {
            ArgumentNullException.ThrowIfNull(lap);
            ArgumentException.ThrowIfNullOrWhiteSpace(grandPrix);

            string folder = Path.Combine(RootPath, UserLapsFolderName, grandPrix);
            Directory.CreateDirectory(folder);

            string baseName = SafeFileName(lapName);
            string name = baseName;
            for (int i = 2; File.Exists(Path.Combine(folder, name + FileExtension)); i++) name = $"{baseName} ({i})";

            Import.LapParquetWriter.Write(lap, Path.Combine(folder, name + FileExtension));

            string trackJson = Path.Combine(folder, TrackLayout.FileName);
            if (!File.Exists(trackJson))
            {
                string? source = ListDirectories(RootPath)
                    .Where(IsSeason)
                    .OrderByDescending(year => year)
                    .Select(year => Path.Combine(RootPath, year, grandPrix, TrackLayout.FileName))
                    .FirstOrDefault(File.Exists);
                if (source != null) File.Copy(source, trackJson);
            }

            return name;
        }

        public static bool IsSeason(string folderName) => int.TryParse(folderName, out _);

        private static string SafeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "My lap" : safe;
        }

        // Tracks recorded for a given year that contain at least one driver lap, alphabetical.
        public IReadOnlyList<string> GetTracks(string year)
        {
            string yearPath = Path.Combine(RootPath, year);
            return ListDirectories(yearPath)
                .Where(track => GetDrivers(year, track).Count > 0)
                .OrderBy(track => track, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Driver codes with a reference lap for a given year and track, alphabetical.
        public IReadOnlyList<string> GetDrivers(string year, string track)
        {
            string trackPath = Path.Combine(RootPath, year, track);
            if (!Directory.Exists(trackPath)) return [];

            return Directory.EnumerateFiles(trackPath, "*" + FileExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .OrderBy(driver => driver, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string GetLapPath(string year, string track, string driver)
        {
            return Path.Combine(RootPath, year, track, driver + FileExtension);
        }

        // Sector/corner/map layout for a Grand Prix, or null when none exists. A folder without its own
        // track.json (e.g. laps dropped into My Laps by hand) uses the newest season's for the same Grand Prix.
        public TrackLayout? GetTrackLayout(string year, string track)
        {
            string path = Path.Combine(RootPath, year, track, TrackLayout.FileName);
            if (File.Exists(path)) return TrackLayout.Load(path);

            string? fallback = ListDirectories(RootPath)
                .Where(IsSeason)
                .OrderByDescending(y => y)
                .Select(y => Path.Combine(RootPath, y, track, TrackLayout.FileName))
                .FirstOrDefault(File.Exists);
            return fallback != null ? TrackLayout.Load(fallback) : null;
        }

        // Walks up from each start directory looking for a 'references' folder.
        // Lets the library be found from bin/Debug/... as well as the repository root.
        public static ReferenceLibrary? Find(params string[] startDirectories)
        {
            foreach (string start in startDirectories)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, DefaultFolderName);
                    if (Directory.Exists(candidate))
                    {
                        return new ReferenceLibrary(candidate);
                    }
                    dir = dir.Parent;
                }
            }

            return null;
        }

        private static IEnumerable<string> ListDirectories(string path)
        {
            if (!Directory.Exists(path)) return [];
            return Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>();
        }
    }
}
