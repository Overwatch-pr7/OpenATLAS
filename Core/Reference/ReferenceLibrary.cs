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
    public class ReferenceLibrary
    {
        public const string DefaultFolderName = "references";
        private const string FileExtension = ".parquet";

        public string RootPath { get; }

        public ReferenceLibrary(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            RootPath = rootPath;
        }

        // Years that contain at least one track folder, newest first.
        public IReadOnlyList<string> GetYears()
        {
            return ListDirectories(RootPath)
                .Where(year => GetTracks(year).Count > 0)
                .OrderByDescending(year => year, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
