using System.IO;
using Xunit;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Tests.Reference
{
    /// <summary>
    /// Verifies track.json parsing and the per-sector corner lookup used by the sector analysis panel.
    /// </summary>
    public class TrackLayoutTests
    {
        // Monza-like layout: three sectors, a split chicane (T1/T2) and one hand-named corner
        public const string MonzaJson = """
            {
              "event": "Italian Grand Prix",
              "location": "Monza",
              "lengthMeters": 5763,
              "sectors": [
                { "number": 1, "startMeters": 0, "endMeters": 1800 },
                { "number": 2, "startMeters": 1800, "endMeters": 3700 },
                { "number": 3, "startMeters": 3700, "endMeters": 5763 }
              ],
              "corners": [
                { "number": 1, "letter": "", "distanceMeters": 895, "name": "Prima Variante" },
                { "number": 2, "letter": "", "distanceMeters": 953, "name": "" },
                { "number": 3, "letter": "", "distanceMeters": 1477, "name": "" },
                { "number": 4, "letter": "", "distanceMeters": 2110, "name": "" },
                { "number": 7, "letter": "", "distanceMeters": 2849, "name": "" },
                { "number": 11, "letter": "", "distanceMeters": 5200, "name": "Parabolica" }
              ]
            }
            """;

        public static void WriteMonzaLayout(string grandPrixFolder) =>
            File.WriteAllText(Path.Combine(grandPrixFolder, TrackLayout.FileName), MonzaJson);

        [Fact]
        public void Load_ParsesSectorsAndCorners()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", "Italian Grand Prix"));

            TrackLayout? layout = temp.Library.GetTrackLayout("2021", "Italian Grand Prix");

            Assert.NotNull(layout);
            Assert.Equal("Monza", layout.Location);
            Assert.Equal(3, layout.Sectors.Count);
            Assert.Equal(new TrackSector(2, 1800, 3700), layout.Sectors[1]);
            Assert.Equal(6, layout.Corners.Count);
        }

        [Fact]
        public void CornersIn_ReturnsOnlyCornersInsideTheSector()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", "Italian Grand Prix"));
            var layout = temp.Library.GetTrackLayout("2021", "Italian Grand Prix")!;

            Assert.Equal(["T1", "T2", "T3"], layout.CornersIn(layout.Sectors[0]).Select(c => c.Label));
            Assert.Equal(["T4", "T7"], layout.CornersIn(layout.Sectors[1]).Select(c => c.Label));
            Assert.Equal(["T11"], layout.CornersIn(layout.Sectors[2]).Select(c => c.Label));
        }

        [Fact]
        public void GetTrackLayout_ReturnsNullWhenFolderHasNoTrackJson()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");

            Assert.Null(temp.Library.GetTrackLayout("2021", "Italian Grand Prix"));
        }

        [Fact]
        public void Load_RejectsLayoutWithoutSectors()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            string path = Path.Combine(temp.RootPath, "2021", "Italian Grand Prix", TrackLayout.FileName);
            File.WriteAllText(path, """{ "event": "x", "sectors": [] }""");

            Assert.Throws<InvalidDataException>(() => TrackLayout.Load(path));
        }

        [Fact]
        public void TrackLibrary_EveryGrandPrixHasThreeOrderedSectors()
        {
            var library = ReferenceLibrary.Find(System.AppContext.BaseDirectory)!;

            foreach (string track in library.GetTracks("2021"))
            {
                TrackLayout? layout = library.GetTrackLayout("2021", track);
                Assert.True(layout != null, $"{track} has no track.json");
                Assert.Equal(3, layout.Sectors.Count);
                Assert.Equal(0f, layout.Sectors[0].StartMeters);
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(layout.Sectors[i].EndMeters > layout.Sectors[i].StartMeters, $"{track} sector {i + 1} is empty");
                    if (i > 0) Assert.Equal(layout.Sectors[i - 1].EndMeters, layout.Sectors[i].StartMeters);
                }
            }
        }
    }
}
