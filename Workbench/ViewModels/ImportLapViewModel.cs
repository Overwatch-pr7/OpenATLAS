using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenATLAS.Core.Import;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// File > Import Lap dialog: reads a CSV log, shows what was detected, lets the user pick a lap,
    /// the Grand Prix it belongs to and a name, then saves it to My Laps.
    /// </summary>
    public class ImportLapViewModel : ViewModelBase
    {
        /// <summary>The accepted file format, shown in the dialog and under Help > Import File Format.</summary>
        public const string FormatDescription =
            "OpenATLAS imports telemetry logs saved as CSV (comma, semicolon or tab separated) with a header row.\n\n" +
            "REQUIRED COLUMNS\n" +
            "  Speed            km/h, m/s or mph\n" +
            "                   (unit from the header, e.g. \"Speed [km/h]\", or a units row)\n" +
            "  Time             seconds (or ms)       } at least one of these two\n" +
            "  Distance         metres (or km)        }\n\n" +
            "OPTIONAL COLUMNS\n" +
            "  Throttle, Brake  0-100 % or 0-1 (scaled automatically)\n" +
            "  Gear, RPM\n" +
            "  Lap              lap number, used to split the log into laps\n\n" +
            "Column names are matched loosely: \"Throttle Pos\", \"throttle\" and \"Gas\" all work.\n" +
            "Without a Lap column, laps are split where the distance resets to zero.\n\n" +
            "WORKS WITH\n" +
            "  Telemetrick (CSV export), Sim Racing Telemetry (Export to CSV),\n" +
            "  MoTeC i2 (File > Export > CSV), or any spreadsheet in this shape.\n\n" +
            "Each imported lap is normalized like the F1 reference laps (time and distance from zero,\n" +
            "one sample per metre) and saved to My Laps under the Grand Prix you choose.";

        private readonly WorkbenchViewModel _workbench;

        private string _filePath = "";
        private CsvImportResult? _result;
        private IReadOnlyList<LapChoice> _laps = [];
        private LapChoice? _selectedLap;
        private string? _selectedGrandPrix;
        private string _lapName = "";
        private string _error = "";

        public ImportLapViewModel(WorkbenchViewModel workbench)
        {
            _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
            GrandPrixOptions = workbench.ImportGrandPrixOptions;
            _selectedGrandPrix = workbench.SelectedGrandPrix != null && GrandPrixOptions.Contains(workbench.SelectedGrandPrix)
                ? workbench.SelectedGrandPrix
                : GrandPrixOptions.FirstOrDefault();
        }

        /// <summary>One lap in the picker, e.g. "LAP 3   1:21.345   5,793 m".</summary>
        public sealed record LapChoice(ImportedLap Lap)
        {
            public string Label =>
                $"LAP {Lap.Number}   {TimeSpan.FromSeconds(Lap.LapTimeSeconds):m\\:ss\\.fff}   {Lap.LengthMeters:N0} m" +
                (Lap.Complete ? "" : "   (out/in lap)");
        }

        public IReadOnlyList<string> GrandPrixOptions { get; }

        public string FilePath { get => _filePath; private set => SetProperty(ref _filePath, value); }

        /// <summary>Detected columns, e.g. "Speed: Speed [km/h]", one per line, with missing ones marked.</summary>
        public string DetectedColumns => _result == null ? "" : string.Join("\n",
            Enum.GetValues<LapChannel>().Select(c => $"{c,-9} {(_result.Columns.TryGetValue(c, out string? h) ? h : "not found")}"));

        public string Warnings => _result == null ? "" : string.Join("\n", _result.Warnings);

        public IReadOnlyList<LapChoice> Laps { get => _laps; private set => SetProperty(ref _laps, value); }

        public LapChoice? SelectedLap
        {
            get => _selectedLap;
            set
            {
                if (!SetProperty(ref _selectedLap, value)) return;
                if (value != null) LapName = $"{Path.GetFileNameWithoutExtension(FilePath)} L{value.Lap.Number}";
                OnPropertyChanged(nameof(CanImport));
            }
        }

        public string? SelectedGrandPrix
        {
            get => _selectedGrandPrix;
            set
            {
                if (SetProperty(ref _selectedGrandPrix, value)) OnPropertyChanged(nameof(CanImport));
            }
        }

        public string LapName
        {
            get => _lapName;
            set
            {
                if (SetProperty(ref _lapName, value)) OnPropertyChanged(nameof(CanImport));
            }
        }

        /// <summary>Why the file could not be read (empty when it was).</summary>
        public string Error { get => _error; private set => SetProperty(ref _error, value); }

        public bool IsLoaded => _result != null;

        public bool CanImport => SelectedLap != null && !string.IsNullOrWhiteSpace(SelectedGrandPrix) && !string.IsNullOrWhiteSpace(LapName);

        /// <summary>Reads the log and preselects its fastest complete lap. Problems end up in <see cref="Error"/>.</summary>
        public void LoadFile(string path)
        {
            FilePath = path;
            Error = "";
            _result = null;
            Laps = [];
            SelectedLap = null;

            try
            {
                _result = CsvLapImporter.Load(path);
                if (_result.Laps.Count == 0)
                {
                    Error = "No laps of at least 1 km were found in this file.";
                }
                else
                {
                    Laps = [.. _result.Laps.Select(l => new LapChoice(l))];
                    ImportedLap best = _result.FastestLap ?? _result.Laps.MinBy(l => l.LapTimeSeconds)!;
                    SelectedLap = Laps.First(c => c.Lap == best);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
            }

            OnPropertyChanged(nameof(DetectedColumns));
            OnPropertyChanged(nameof(Warnings));
            OnPropertyChanged(nameof(IsLoaded));
        }

        /// <summary>Saves the selected lap to My Laps. Returns the saved name, or null with <see cref="Error"/> set.</summary>
        public string? Import()
        {
            if (!CanImport) return null;
            try
            {
                return _workbench.SaveImportedLap(SelectedLap!.Lap.Samples, SelectedGrandPrix!, LapName);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                Error = ex.Message;
                return null;
            }
        }
    }
}
