using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenATLAS.Core.Import
{
    public enum LapChannel { Time, Distance, Speed, Throttle, Brake, Gear, Rpm, Lap }

    // One lap found in a log. Complete is false for laps much shorter/longer than the rest
    // (out-laps, in-laps, laps cut short by a pause or crash).
    public sealed record ImportedLap(int Number, double LapTimeSeconds, double LengthMeters, bool Complete,
                                     IReadOnlyList<RawLapSample> Samples);

    public sealed class CsvImportResult
    {
        // Channel -> column header it was read from
        public IReadOnlyDictionary<LapChannel, string> Columns { get; init; } = new Dictionary<LapChannel, string>();
        public string SpeedUnit { get; init; } = "km/h";
        public IReadOnlyList<string> Warnings { get; init; } = [];
        public IReadOnlyList<ImportedLap> Laps { get; init; } = [];

        // Fastest complete lap: the sensible default to import
        public ImportedLap? FastestLap => Laps.Where(l => l.Complete).MinBy(l => l.LapTimeSeconds);
    }

    // CSV Lap Importer
    // Reads telemetry logs exported by common sim tools (Telemetrick, Sim Racing Telemetry, MoTeC i2
    // "Export CSV", or any spreadsheet with a header row) and splits them into laps.
    //
    // Required columns: speed plus time and/or distance. Optional: throttle, brake, gear, RPM, lap number.
    // Column names are matched loosely ("Throttle Pos", "throttle", "Gas" all work) and units are
    // detected from "[unit]"/"(unit)" suffixes or a units row under the header (MoTeC style).
    public static class CsvLapImporter
    {
        // Normalized header name -> channel. Headers are lower-cased with units and punctuation removed.
        private static readonly Dictionary<string, LapChannel> Aliases = new()
        {
            ["time"] = LapChannel.Time, ["t"] = LapChannel.Time, ["timestamp"] = LapChannel.Time,
            ["sessiontime"] = LapChannel.Time, ["elapsedtime"] = LapChannel.Time,
            ["currentlaptime"] = LapChannel.Time, ["laptimecurrent"] = LapChannel.Time,

            ["distance"] = LapChannel.Distance, ["dist"] = LapChannel.Distance, ["lapdistance"] = LapChannel.Distance,
            ["lapdist"] = LapChannel.Distance, ["distancetraveled"] = LapChannel.Distance,

            ["speed"] = LapChannel.Speed, ["speedkmh"] = LapChannel.Speed, ["speedkph"] = LapChannel.Speed,
            ["groundspeed"] = LapChannel.Speed, ["vehiclespeed"] = LapChannel.Speed, ["carspeed"] = LapChannel.Speed,
            ["speedmph"] = LapChannel.Speed, ["speedms"] = LapChannel.Speed,

            ["throttle"] = LapChannel.Throttle, ["throttlepos"] = LapChannel.Throttle, ["throttleposition"] = LapChannel.Throttle,
            ["throttlepedal"] = LapChannel.Throttle, ["gas"] = LapChannel.Throttle, ["accelerator"] = LapChannel.Throttle,

            ["brake"] = LapChannel.Brake, ["brakepos"] = LapChannel.Brake, ["brakeposition"] = LapChannel.Brake,
            ["brakepedal"] = LapChannel.Brake,

            ["gear"] = LapChannel.Gear, ["ngear"] = LapChannel.Gear, ["gearselected"] = LapChannel.Gear,

            ["rpm"] = LapChannel.Rpm, ["rpms"] = LapChannel.Rpm, ["enginerpm"] = LapChannel.Rpm,

            ["lap"] = LapChannel.Lap, ["lapnumber"] = LapChannel.Lap, ["lapcount"] = LapChannel.Lap,
            ["completedlaps"] = LapChannel.Lap, ["laps"] = LapChannel.Lap,
        };

        // Header rows are looked for within the first lines (MoTeC exports start with a metadata block)
        private const int MaxHeaderSearchLines = 60;

        // A lap within this share of the median lap length counts as complete
        private const double CompleteLapTolerance = 0.05;

        public static CsvImportResult Load(string filePath)
        {
            using var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true);
            return Parse(reader);
        }

        public static CsvImportResult Parse(TextReader reader)
        {
            var lines = new List<string>();
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine()) lines.Add(line);

            var warnings = new List<string>();
            (int headerIndex, char delimiter, Dictionary<LapChannel, int> columns, string[] header) = FindHeader(lines);

            if (!columns.ContainsKey(LapChannel.Speed) ||
                !(columns.ContainsKey(LapChannel.Time) || columns.ContainsKey(LapChannel.Distance)))
            {
                throw new InvalidDataException(
                    "Could not find the required columns. The file needs a header row with a speed column " +
                    "and a time and/or distance column (see Help > Import File Format).");
            }

            // Optional units row directly under the header (MoTeC i2 exports have one)
            string[]? units = null;
            int dataStart = headerIndex + 1;
            if (dataStart < lines.Count)
            {
                string[] candidate = Split(lines[dataStart], delimiter);
                if (columns.Values.Any(c => c < candidate.Length && candidate[c].Length > 0 && !IsNumber(candidate[c])))
                {
                    units = candidate;
                    dataStart++;
                }
            }

            string UnitOf(LapChannel channel) =>
                ExtractUnit(header[columns[channel]]) ??
                (units != null && columns[channel] < units.Length ? units[columns[channel]].Trim().ToLowerInvariant() : "");

            // Units -> factors to s, m, km/h
            double speedFactor = SpeedFactor(columns, header, UnitOf(LapChannel.Speed), out string speedUnit, warnings);
            double distanceFactor = columns.ContainsKey(LapChannel.Distance) && UnitOf(LapChannel.Distance) == "km" ? 1000 : 1;

            var rows = ReadRows(lines, dataStart, delimiter, columns, warnings);
            if (rows.Count < 10) throw new InvalidDataException($"Only {rows.Count} data rows could be read.");

            double timeFactor = 1;
            if (columns.ContainsKey(LapChannel.Time))
            {
                string timeUnit = UnitOf(LapChannel.Time);
                if (timeUnit == "ms" || (timeUnit == "" && MedianStep(rows, LapChannel.Time) >= 5))
                {
                    timeFactor = 0.001; // milliseconds (or timestamps far too far apart to be seconds)
                }
            }
            else
            {
                warnings.Add("No time column: lap times cannot be computed, so the delta will be meaningless.");
            }

            // Pedals logged as 0-1 are scaled to 0-100 %
            double throttleFactor = PedalFactor(rows, LapChannel.Throttle);
            double brakeFactor = PedalFactor(rows, LapChannel.Brake);
            if (!columns.ContainsKey(LapChannel.Throttle)) warnings.Add("No throttle column: throttle will show as 0 %.");
            if (!columns.ContainsKey(LapChannel.Brake)) warnings.Add("No brake column: brake will show as 0 %.");

            var samples = rows.Select(r => new RawLapSample(
                TimeSeconds: r.Get(LapChannel.Time) * timeFactor,
                DistanceMeters: columns.ContainsKey(LapChannel.Distance) ? r.Get(LapChannel.Distance) * distanceFactor : double.NaN,
                SpeedKmh: r.Get(LapChannel.Speed) * speedFactor,
                ThrottlePercent: r.Get(LapChannel.Throttle) * throttleFactor,
                BrakePercent: r.Get(LapChannel.Brake) * brakeFactor,
                Gear: (int)Math.Round(r.Get(LapChannel.Gear)),
                Rpm: (int)Math.Round(r.Get(LapChannel.Rpm)))).ToList();

            // Without a time column, synthesize one from distance and speed so laps can be normalized
            if (!columns.ContainsKey(LapChannel.Time)) samples = TimeFromDistance(samples);

            var laps = SplitLaps(samples, rows, columns.ContainsKey(LapChannel.Lap));

            return new CsvImportResult
            {
                Columns = columns.ToDictionary(c => c.Key, c => header[c.Value].Trim()),
                SpeedUnit = speedUnit,
                Warnings = warnings,
                Laps = laps,
            };
        }

        private static (int, char, Dictionary<LapChannel, int>, string[]) FindHeader(List<string> lines)
        {
            for (int i = 0; i < Math.Min(lines.Count, MaxHeaderSearchLines); i++)
            {
                char delimiter = DetectDelimiter(lines[i]);
                string[] cells = Split(lines[i], delimiter);
                var columns = new Dictionary<LapChannel, int>();

                for (int c = 0; c < cells.Length; c++)
                {
                    if (Aliases.TryGetValue(NormalizeHeader(cells[c]), out LapChannel channel) && !columns.ContainsKey(channel))
                    {
                        columns[channel] = c;
                    }
                }

                if (columns.ContainsKey(LapChannel.Speed) && columns.Count >= 2) return (i, delimiter, columns, cells);
            }

            return (-1, ',', [], []);
        }

        private static char DetectDelimiter(string line)
        {
            char[] candidates = [',', ';', '\t'];
            return candidates.MaxBy(d => line.Count(ch => ch == d));
        }

        // Splits a CSV line, honouring double quotes
        private static string[] Split(string line, char delimiter)
        {
            var cells = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            foreach (char ch in line)
            {
                if (ch == '"') quoted = !quoted;
                else if (ch == delimiter && !quoted) { cells.Add(current.ToString().Trim()); current.Clear(); }
                else current.Append(ch);
            }
            cells.Add(current.ToString().Trim());
            return [.. cells];
        }

        // "Throttle Pos [%]" -> "throttlepos"; "Speed (km/h)" -> "speed"; "speed_kmh" -> "speedkmh"
        private static string NormalizeHeader(string header)
        {
            string withoutUnit = header;
            int bracket = header.IndexOfAny(['[', '(']);
            if (bracket > 0) withoutUnit = header[..bracket];
            return new string(withoutUnit.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static string? ExtractUnit(string header)
        {
            int open = header.IndexOfAny(['[', '(']);
            int close = header.IndexOfAny([']', ')']);
            return open >= 0 && close > open ? header[(open + 1)..close].Trim().ToLowerInvariant() : null;
        }

        private static double SpeedFactor(Dictionary<LapChannel, int> columns, string[] header, string unit,
                                          out string speedUnit, List<string> warnings)
        {
            string name = NormalizeHeader(header[columns[LapChannel.Speed]]);
            unit = unit.Replace(" ", "");

            if (unit is "m/s" or "ms" or "mps" || name == "speedms") { speedUnit = "m/s"; return 3.6; }
            if (unit is "mph" || name == "speedmph") { speedUnit = "mph"; return 1.609344; }
            if (unit is "km/h" or "kmh" or "kph" || name is "speedkmh" or "speedkph") { speedUnit = "km/h"; return 1; }

            speedUnit = "km/h";
            warnings.Add("Speed unit not stated in the file; assuming km/h.");
            return 1;
        }

        private sealed class Row
        {
            public readonly Dictionary<LapChannel, double> Values = [];
            public double Get(LapChannel channel) => Values.TryGetValue(channel, out double v) ? v : 0;
        }

        private static List<Row> ReadRows(List<string> lines, int start, char delimiter,
                                          Dictionary<LapChannel, int> columns, List<string> warnings)
        {
            // Semicolon-separated files often use a decimal comma (European locale)
            bool decimalComma = delimiter == ';';
            var rows = new List<Row>();
            int skipped = 0;

            for (int i = start; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] cells = Split(lines[i], delimiter);
                var row = new Row();
                bool valid = true;

                foreach (var (channel, column) in columns)
                {
                    string cell = column < cells.Length ? cells[column] : "";
                    if (decimalComma) cell = cell.Replace(',', '.');
                    if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    {
                        row.Values[channel] = value;
                    }
                    else if (channel is LapChannel.Speed or LapChannel.Time or LapChannel.Distance)
                    {
                        valid = false; // a row without its core channels is unusable
                        break;
                    }
                }

                if (valid) rows.Add(row); else skipped++;
            }

            if (skipped > 0) warnings.Add($"{skipped} row(s) without valid speed/time/distance values were skipped.");
            return rows;
        }

        private static double MedianStep(List<Row> rows, LapChannel channel)
        {
            var steps = new List<double>();
            for (int i = 1; i < Math.Min(rows.Count, 500); i++)
            {
                double step = rows[i].Get(channel) - rows[i - 1].Get(channel);
                if (step > 0) steps.Add(step);
            }
            if (steps.Count == 0) return 0;
            steps.Sort();
            return steps[steps.Count / 2];
        }

        private static double PedalFactor(List<Row> rows, LapChannel channel)
        {
            double max = rows.Max(r => r.Get(channel));
            return max > 0 && max <= 1.0 ? 100 : 1;
        }

        private static List<RawLapSample> TimeFromDistance(List<RawLapSample> samples)
        {
            var result = new List<RawLapSample>(samples.Count) { samples[0] with { TimeSeconds = 0 } };
            for (int i = 1; i < samples.Count; i++)
            {
                double ds = Math.Max(0, samples[i].DistanceMeters - samples[i - 1].DistanceMeters);
                double v = Math.Max(1.0, (samples[i].SpeedKmh + samples[i - 1].SpeedKmh) / 2 / 3.6);
                result.Add(samples[i] with { TimeSeconds = result[^1].TimeSeconds + ds / v });
            }
            return result;
        }

        // Splits by the lap-number column, else by distance resets, else the whole log is one lap
        private static List<ImportedLap> SplitLaps(List<RawLapSample> samples, List<Row> rows, bool hasLapColumn)
        {
            var groups = new List<List<RawLapSample>>();
            var current = new List<RawLapSample> { samples[0] };

            for (int i = 1; i < samples.Count; i++)
            {
                bool newLap = hasLapColumn
                    ? rows[i].Get(LapChannel.Lap) != rows[i - 1].Get(LapChannel.Lap)
                    : !double.IsNaN(samples[i].DistanceMeters) && samples[i].DistanceMeters < samples[i - 1].DistanceMeters - 100;

                if (newLap)
                {
                    groups.Add(current);
                    current = [];
                }
                current.Add(samples[i]);
            }
            groups.Add(current);

            var laps = new List<ImportedLap>();
            foreach (var group in groups.Where(g => g.Count >= 10))
            {
                double length = LapLength(group);
                if (length < LapNormalizer.MinLapLengthMeters) continue;
                laps.Add(new ImportedLap(laps.Count + 1, group[^1].TimeSeconds - group[0].TimeSeconds, length, true, group));
            }

            // With three or more laps, out-laps and in-laps stand out by length
            if (laps.Count >= 3)
            {
                double median = laps.Select(l => l.LengthMeters).OrderBy(x => x).ElementAt(laps.Count / 2);
                laps = [.. laps.Select(l => l with { Complete = Math.Abs(l.LengthMeters - median) <= median * CompleteLapTolerance })];
            }

            return laps;
        }

        private static double LapLength(List<RawLapSample> lap)
        {
            if (!double.IsNaN(lap[0].DistanceMeters)) return lap[^1].DistanceMeters - lap[0].DistanceMeters;

            double distance = 0;
            for (int i = 1; i < lap.Count; i++)
            {
                distance += (lap[i].TimeSeconds - lap[i - 1].TimeSeconds) * (lap[i].SpeedKmh + lap[i - 1].SpeedKmh) / 2 / 3.6;
            }
            return distance;
        }

        private static bool IsNumber(string cell) =>
            double.TryParse(cell.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}
