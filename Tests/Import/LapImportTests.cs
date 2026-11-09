using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using OpenATLAS.Core.Import;
using OpenATLAS.Core.Reference;
using OpenATLAS.Tests.Reference;

namespace OpenATLAS.Tests.Import
{
    /// <summary>
    /// Verifies the user lap pipeline: CSV parsing, normalization to one sample per metre,
    /// Parquet output compatible with the F1 reference laps, live recording, and the My Laps library.
    /// </summary>
    public class LapImportTests
    {
        // Constant-speed lap: 180 km/h = 50 m/s, sampled at 20 Hz for the given duration
        private static List<RawLapSample> ConstantSpeedLap(double seconds, bool withDistance, double startTime = 0)
        {
            var samples = new List<RawLapSample>();
            for (int i = 0; i <= seconds * 20; i++)
            {
                double t = i / 20.0;
                samples.Add(new RawLapSample(startTime + t, withDistance ? t * 50 : double.NaN, 180, 100, 0, 5, 9000));
            }
            return samples;
        }

        // CSV of several laps on a 3,000 m track at 20 Hz. Lap 1 is an out-lap that starts at 1,200 m;
        // lap 3 is the fastest. Speed varies along the lap so the lap times differ.
        internal static string MultiLapCsv(string speedHeader = "Speed [km/h]", double speedScale = 1, bool pedals01 = false,
                                          char delimiter = ',', bool decimalComma = false, bool withLapColumn = true)
        {
            var sb = new StringBuilder();
            string[] header = ["Time [s]", "Lap Distance [m]", speedHeader, "Throttle Pos", "Brake", "Gear", "Engine RPM"];
            if (withLapColumn) header = [.. header, "Lap"];
            sb.AppendLine(string.Join(delimiter, header));

            double time = 0;
            double[] lapSpeeds = [150, 180, 200, 170]; // km/h per lap; lap 3 (200) is fastest
            for (int lap = 1; lap <= 4; lap++)
            {
                double distance = lap == 1 ? 1200 : 0;
                double v = lapSpeeds[lap - 1];
                while (distance < 3000)
                {
                    double pedal = pedals01 ? 1.0 : 100.0;
                    string[] cells =
                    [
                        F(time), F(distance), F(v * speedScale), F(pedal), F(0), "4", "8500",
                    ];
                    if (withLapColumn) cells = [.. cells, lap.ToString(CultureInfo.InvariantCulture)];
                    string line = string.Join(delimiter, cells);
                    sb.AppendLine(decimalComma ? line.Replace('.', ',') : line);

                    time += 0.05;
                    distance += v / 3.6 * 0.05;
                }
            }
            return sb.ToString();

            static string F(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static CsvImportResult Parse(string csv) => CsvLapImporter.Parse(new StringReader(csv));

        // ---------------------------------------------------------------- Normalizer

        [Fact]
        public void Normalize_IntegratesDistanceFromSpeed_AndResamplesPerMetre()
        {
            ReferenceLapIndex lap = LapNormalizer.Normalize(ConstantSpeedLap(60, withDistance: false));

            Assert.Equal(3000, lap.TrackLengthMeters);
            Assert.Equal(0f, lap.Time[0]);
            Assert.Equal(30f, lap.GetTimeAtDistance(1500), 2);
            Assert.Equal(180f, lap.Speed[1234], 2);
            Assert.Equal(100, lap.Throttle[10]);
            Assert.Equal(5, lap.Gear[10]);
        }

        [Fact]
        public void Normalize_ZeroesTimeAndDistanceAtTheStartOfTheLap()
        {
            var samples = ConstantSpeedLap(60, withDistance: true, startTime: 500)
                .Select(s => s with { DistanceMeters = s.DistanceMeters + 7000 }).ToList();

            ReferenceLapIndex lap = LapNormalizer.Normalize(samples);

            Assert.Equal(0f, lap.Time[0]);
            Assert.Equal(3000, lap.TrackLengthMeters);
            Assert.Equal(60f, lap.TotalLapTimeSeconds, 2);
        }

        [Fact]
        public void Normalize_RejectsPartialLaps()
        {
            Assert.Throws<ArgumentException>(() => LapNormalizer.Normalize(ConstantSpeedLap(10, withDistance: true))); // 500 m
        }

        [Fact]
        public void Writer_RoundTripsThroughTheReferenceLapReader()
        {
            ReferenceLapIndex lap = LapNormalizer.Normalize(ConstantSpeedLap(60, withDistance: false));
            string path = Path.Combine(Path.GetTempPath(), $"openatlas-lap-{Guid.NewGuid():N}.parquet");
            try
            {
                LapParquetWriter.Write(lap, path);
                ReferenceLapIndex read = ReferenceLapIndex.FromParquetFile(path);

                Assert.Equal(lap.Distance, read.Distance);
                Assert.Equal(lap.Speed, read.Speed);
                Assert.Equal(lap.Throttle, read.Throttle);
                Assert.Equal(lap.Brake, read.Brake);
                Assert.Equal(lap.Rpm, read.Rpm);
                Assert.Equal(lap.Gear, read.Gear);
                Assert.Equal(lap.Time, read.Time);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ---------------------------------------------------------------- CSV importer

        [Fact]
        public void Csv_DetectsColumnsAndSplitsLapsByLapNumber()
        {
            CsvImportResult result = Parse(MultiLapCsv());

            Assert.Equal("Speed [km/h]", result.Columns[LapChannel.Speed]);
            Assert.Equal("Throttle Pos", result.Columns[LapChannel.Throttle]);
            Assert.Equal("Engine RPM", result.Columns[LapChannel.Rpm]);
            Assert.Equal(4, result.Laps.Count);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void Csv_FlagsOutLap_AndPicksFastestCompleteLap()
        {
            CsvImportResult result = Parse(MultiLapCsv());

            Assert.False(result.Laps[0].Complete);                // out-lap: started at 1,200 m
            Assert.All(result.Laps.Skip(1), l => Assert.True(l.Complete));
            Assert.Equal(3, result.FastestLap!.Number);
            Assert.Equal(3000 / (200 / 3.6), result.FastestLap.LapTimeSeconds, 0);
        }

        [Fact]
        public void Csv_ConvertsSpeedUnitsAndPedalRange()
        {
            CsvImportResult result = Parse(MultiLapCsv(speedHeader: "Speed (m/s)", speedScale: 1 / 3.6, pedals01: true));

            Assert.Equal("m/s", result.SpeedUnit);
            RawLapSample sample = result.FastestLap!.Samples[50];
            Assert.Equal(200, sample.SpeedKmh, 1);
            Assert.Equal(100, sample.ThrottlePercent, 1);
        }

        [Fact]
        public void Csv_ReadsSemicolonsWithDecimalCommas()
        {
            CsvImportResult result = Parse(MultiLapCsv(delimiter: ';', decimalComma: true));

            Assert.Equal(4, result.Laps.Count);
            Assert.Equal(200, result.FastestLap!.Samples[10].SpeedKmh, 1);
        }

        [Fact]
        public void Csv_WithoutLapColumn_SplitsOnDistanceResets()
        {
            CsvImportResult result = Parse(MultiLapCsv(withLapColumn: false));

            Assert.Equal(4, result.Laps.Count);
            Assert.Equal(3, result.FastestLap!.Number);
        }

        [Fact]
        public void Csv_MotecExport_SkipsMetadataAndReadsUnitsRow()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"Format\",\"MoTeC CSV File\"");
            sb.AppendLine("\"Venue\",\"monza\"");
            sb.AppendLine("\"Vehicle\",\"ks_ferrari_sf15t\"");
            sb.AppendLine();
            sb.AppendLine("\"Time\",\"Distance\",\"Ground Speed\",\"Throttle Pos\",\"Brake Pos\",\"Gear\",\"Engine RPM\"");
            sb.AppendLine("\"s\",\"km\",\"m/s\",\"%\",\"%\",\"\",\"rpm\"");
            for (int i = 0; i <= 1200; i++)
            {
                double t = i * 0.05;
                sb.AppendLine(string.Join(",", F(t), F(t * 50 / 1000), F(50), "100", "0", "6", "10000"));
            }

            CsvImportResult result = Parse(sb.ToString());

            Assert.Equal("m/s", result.SpeedUnit);
            ImportedLap lap = Assert.Single(result.Laps);
            Assert.Equal(3000, lap.LengthMeters, 0);         // km converted to m
            Assert.Equal(180, lap.Samples[100].SpeedKmh, 1);  // m/s converted to km/h

            static string F(double x) => x.ToString("0.####", CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Csv_WarnsAboutMissingOptionalChannelsAndUnits()
        {
            var csv = new StringBuilder("time,speed\n");
            for (int i = 0; i <= 1200; i++) csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{i * 0.05:0.00},180"));

            CsvImportResult result = Parse(csv.ToString());

            Assert.Single(result.Laps);
            Assert.Contains(result.Warnings, w => w.Contains("assuming km/h"));
            Assert.Contains(result.Warnings, w => w.Contains("No throttle column"));
            Assert.Contains(result.Warnings, w => w.Contains("No brake column"));
        }

        [Fact]
        public void Csv_WithoutSpeedColumn_IsRejected()
        {
            Assert.Throws<InvalidDataException>(() => Parse("time,throttle\n0,100\n0.05,100\n"));
        }

        [Fact]
        public void Csv_ImportedLapNormalizesIntoAReferenceLap()
        {
            ImportedLap fastest = Parse(MultiLapCsv()).FastestLap!;

            ReferenceLapIndex lap = LapNormalizer.Normalize(fastest.Samples);

            Assert.InRange(lap.TrackLengthMeters, 2990, 3000);
            Assert.Equal(fastest.LapTimeSeconds, lap.TotalLapTimeSeconds, 0);
        }

        // ---------------------------------------------------------------- Recorder

        [Fact]
        public void Recorder_KeepsCompleteLaps_AndDiscardsPartialOnes()
        {
            var recorder = new LapRecorder();
            foreach (var s in ConstantSpeedLap(60, withDistance: true)) recorder.Add(s);
            Assert.NotNull(recorder.FinishLap(referenceLengthMeters: 3000));

            // Joined mid-lap: starts at 1,500 m
            foreach (var s in ConstantSpeedLap(30, withDistance: true)) recorder.Add(s with { DistanceMeters = s.DistanceMeters + 1500 });
            Assert.Null(recorder.FinishLap(3000));

            // Too short: half a lap from the line
            foreach (var s in ConstantSpeedLap(30, withDistance: true)) recorder.Add(s);
            Assert.Null(recorder.FinishLap(3000));
            Assert.Equal(0, recorder.SampleCount);
        }

        // ---------------------------------------------------------------- My Laps library

        [Fact]
        public void SaveUserLap_WritesLap_CopiesTrackJson_AndKeepsNamesUnique()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM");
            TrackLayoutTests.WriteMonzaLayout(Path.Combine(temp.RootPath, "2021", "Italian Grand Prix"));
            ReferenceLibrary library = temp.Library;
            ReferenceLapIndex lap = LapNormalizer.Normalize(ConstantSpeedLap(60, withDistance: false));

            string first = library.SaveUserLap(lap, "Italian Grand Prix", "Ferrari 1:20");
            string second = library.SaveUserLap(lap, "Italian Grand Prix", "Ferrari 1:20");

            Assert.Equal("Ferrari 1_20", first);           // ':' is not allowed in file names
            Assert.Equal("Ferrari 1_20 (2)", second);
            Assert.Equal(["Ferrari 1_20", "Ferrari 1_20 (2)"], library.GetUserLaps("Italian Grand Prix"));
            Assert.NotNull(library.GetTrackLayout(ReferenceLibrary.UserLapsFolderName, "Italian Grand Prix"));
        }

        [Fact]
        public void Library_ListsMyLapsAfterTheSeasons_AndNotAsAGrandPrixSource()
        {
            using var temp = new TempReferenceLibrary("2021/Italian Grand Prix/HAM", "2022/Bahrain Grand Prix/VER");
            ReferenceLibrary library = temp.Library;
            library.SaveUserLap(LapNormalizer.Normalize(ConstantSpeedLap(60, false)), "Italian Grand Prix", "Mine");

            Assert.Equal(["2022", "2021", ReferenceLibrary.UserLapsFolderName], library.GetYears());
            Assert.Equal(["Bahrain Grand Prix", "Italian Grand Prix"], library.GetAllGrandPrix());
        }
    }
}
