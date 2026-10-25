using System;
using System.IO;
using Xunit;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Tests.Reference
{
    // Builds a throwaway references/<Year>/<Track>/<DRIVER>.parquet tree from the real Monza lap.
    public sealed class TempReferenceLibrary : IDisposable
    {
        public static readonly string SourceLapPath =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../references/2023/Monza/HAM.parquet"));

        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "openatlas-refs-" + Guid.NewGuid().ToString("N"));

        public TempReferenceLibrary(params string[] laps)
        {
            Directory.CreateDirectory(RootPath);
            foreach (string lap in laps)
            {
                string target = Path.Combine(RootPath, lap.Replace('/', Path.DirectorySeparatorChar) + ".parquet");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(SourceLapPath, target);
            }
        }

        public ReferenceLibrary Library => new(RootPath);

        public void Dispose()
        {
            try { Directory.Delete(RootPath, recursive: true); } catch (IOException) { }
        }
    }

    public class ReferenceLibraryTests
    {
        [Fact]
        public void GetYears_ReturnsNewestFirst_AndSkipsEmptyYears()
        {
            using var temp = new TempReferenceLibrary("2022/Monza/HAM", "2024/Monza/LEC", "2023/Silverstone/NOR");
            Directory.CreateDirectory(Path.Combine(temp.RootPath, "2021", "Monaco"));

            Assert.Equal(["2024", "2023", "2022"], temp.Library.GetYears());
        }

        [Fact]
        public void GetTracks_ReturnsAlphabeticalTracksForYear()
        {
            using var temp = new TempReferenceLibrary("2023/Silverstone/NOR", "2023/Monza/HAM", "2024/Spa-Francorchamps/VER");

            Assert.Equal(["Monza", "Silverstone"], temp.Library.GetTracks("2023"));
        }

        [Fact]
        public void GetDrivers_ReturnsDriverCodesFromFileNames()
        {
            using var temp = new TempReferenceLibrary("2023/Monza/VER", "2023/Monza/HAM", "2023/Monza/LEC");

            Assert.Equal(["HAM", "LEC", "VER"], temp.Library.GetDrivers("2023", "Monza"));
        }

        [Fact]
        public void UnknownSelections_ReturnEmptyLists()
        {
            using var temp = new TempReferenceLibrary("2023/Monza/HAM");

            Assert.Empty(temp.Library.GetTracks("1999"));
            Assert.Empty(temp.Library.GetDrivers("2023", "Imola"));
        }

        [Fact]
        public void GetLapPath_PointsToLoadableParquet()
        {
            using var temp = new TempReferenceLibrary("2023/Monza/HAM");

            string path = temp.Library.GetLapPath("2023", "Monza", "HAM");
            var lap = ReferenceLapIndex.FromParquetFile(path);

            Assert.Equal(5763, lap.TrackLengthMeters);
        }

        [Fact]
        public void Find_WalksUpToRepositoryReferencesFolder()
        {
            var library = ReferenceLibrary.Find(AppContext.BaseDirectory);

            Assert.NotNull(library);
            Assert.Contains("2023", library.GetYears());
        }

        [Fact]
        public void ExtractedMonzaLap_StoresBrakeAsPercent()
        {
            var lap = ReferenceLapIndex.FromParquetFile(TempReferenceLibrary.SourceLapPath);

            Assert.Equal(100, lap.Brake.Max());
        }
    }
}
