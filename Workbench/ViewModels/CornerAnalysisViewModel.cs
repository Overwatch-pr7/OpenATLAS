using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenATLAS.Core.Analysis;
using OpenATLAS.Core.Import;
using OpenATLAS.Core.Reference;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// Task 5.2: the corner analysis pop-up. Measures every corner of the selected Grand Prix on a
    /// Racer 1 lap and on Racer 2's lap and lists them side by side, with the corners where the most
    /// time is lost highlighted.
    /// </summary>
    public sealed class CornerAnalysisViewModel : ViewModelBase
    {
        public const string CurrentLapOption = "CURRENT LAP";
        private const string CapturedPrefix = "CAPTURED ";
        private const string SavedPrefix = "SAVED: ";
        private const int BiggestLossCount = 3;

        private readonly WorkbenchViewModel _main;
        private readonly Func<double, LapTrace?> _currentLap;

        private IReadOnlyList<string> _racer1Options = [];
        private string? _selectedRacer1;
        private string _summary = "";
        private string _message = "";
        private string _racer2Label = "";

        public ObservableCollection<CornerRowViewModel> Rows { get; } = [];

        /// <param name="main">The workbench, for Racer 2, the track layout and the laps on offer.</param>
        /// <param name="currentLap">The live lap in progress, given the full lap length (null when there is none).</param>
        public CornerAnalysisViewModel(WorkbenchViewModel main, Func<double, LapTrace?> currentLap)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));
            _currentLap = currentLap ?? throw new ArgumentNullException(nameof(currentLap));
            RefreshCommand = new RelayCommand(Refresh);
            RefreshOptions();
        }

        public RelayCommand RefreshCommand { get; }

        /// <summary>Racer 1 laps to analyse: the lap in progress, captured laps and saved laps.</summary>
        public IReadOnlyList<string> Racer1Options
        {
            get => _racer1Options;
            private set => SetProperty(ref _racer1Options, value);
        }

        public string? SelectedRacer1
        {
            get => _selectedRacer1;
            set
            {
                if (SetProperty(ref _selectedRacer1, value))
                {
                    OnPropertyChanged(nameof(IsCurrentLapSelected));
                    Refresh();
                }
            }
        }

        /// <summary>True when the table follows the lap in progress (the window then refreshes it while running).</summary>
        public bool IsCurrentLapSelected => _selectedRacer1 == CurrentLapOption;

        public string Racer2Label
        {
            get => _racer2Label;
            private set => SetProperty(ref _racer2Label, value);
        }

        /// <summary>One line naming the corners where the most time is lost.</summary>
        public string Summary
        {
            get => _summary;
            private set => SetProperty(ref _summary, value);
        }

        /// <summary>Why the table is empty or partly empty; blank when everything was measured.</summary>
        public string Message
        {
            get => _message;
            private set => SetProperty(ref _message, value);
        }

        /// <summary>
        /// Rebuilds the Racer 1 list (after a lap is captured or saved, or the Grand Prix changes)
        /// and keeps the current choice when it is still on offer. The default is Racer 1's own
        /// saved lap when that is the selected source, else the newest captured lap, else the lap in progress.
        /// </summary>
        public void RefreshOptions()
        {
            var options = new List<string> { CurrentLapOption };
            options.AddRange(_main.RecordedLaps.Reverse().Select(CapturedLabel));
            options.AddRange(_main.AvailableSavedLaps.Select(name => SavedPrefix + name));

            string? keep = _selectedRacer1 != null && options.Contains(_selectedRacer1) ? _selectedRacer1 : null;
            string? preferred =
                _main.SelectedRacer1Source == WorkbenchViewModel.Racer1SavedLap && _main.SelectedSavedLap != null
                    ? SavedPrefix + _main.SelectedSavedLap
                    : _main.RecordedLaps.Count > 0 ? CapturedLabel(_main.RecordedLaps[^1]) : CurrentLapOption;

            Racer1Options = options;
            _selectedRacer1 = keep ?? (options.Contains(preferred) ? preferred : CurrentLapOption);
            OnPropertyChanged(nameof(SelectedRacer1));
            OnPropertyChanged(nameof(IsCurrentLapSelected));
            Refresh();
        }

        private static string CapturedLabel(RecordedLap lap) => CapturedPrefix + lap.Label;

        /// <summary>Measures both laps again and rebuilds the table.</summary>
        public void Refresh()
        {
            ReferenceLapIndex? reference = _main.ReferenceLap;
            TrackLayout? layout = _main.TrackLayout;
            Racer2Label = reference != null ? _main.ReferenceLabel : "";

            if (reference == null)
            {
                SetEmpty("Select a Racer 2 lap (year, Grand Prix and driver) first.");
                return;
            }
            if (layout == null || layout.Corners.Count == 0)
            {
                SetEmpty("This Grand Prix has no corner positions in its track.json, so corners cannot be analysed.");
                return;
            }

            // Corner positions are in the layout's metres; each lap is scaled onto them by its length
            double layoutLength = layout.LengthMeters > 0 ? layout.LengthMeters : reference.TrackLengthMeters;
            var zones = CornerAnalyzer.BuildZones(layout.Corners, layoutLength);

            LapTrace? racer1;
            try
            {
                racer1 = LoadRacer1(reference.TrackLengthMeters);
            }
            catch (Exception ex)
            {
                SetEmpty($"Could not read the Racer 1 lap: {ex.Message}");
                return;
            }

            if (racer1 == null)
            {
                SetEmpty("No lap in progress. Press START, or pick a captured or saved lap above.");
                return;
            }

            var refTrace = LapTrace.FromLap(reference);
            var youMetrics = CornerAnalyzer.MeasureAll(racer1, zones, racer1.LengthMeters / layoutLength);
            var refMetrics = CornerAnalyzer.MeasureAll(refTrace, zones, refTrace.LengthMeters / layoutLength);

            var rows = zones.Select((z, i) => new CornerRowViewModel(z, youMetrics[i], refMetrics[i])).ToList();

            var losses = rows.Where(r => r.TimeDeltaSeconds < -CornerRowViewModel.TimeToleranceSeconds)
                             .OrderBy(r => r.TimeDeltaSeconds)
                             .Take(BiggestLossCount)
                             .ToList();
            foreach (var row in losses) row.IsBiggestLoss = true;

            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);

            Summary = losses.Count == 0
                ? (rows.Any(r => r.TimeDeltaSeconds.HasValue) ? "No corner loses more than 0.05s. On pace everywhere measured." : "")
                : "MOST TIME LOST: " + string.Join("   ", losses.Select(r => $"{r.Corner} {r.TimeDelta}"));

            int reached = youMetrics.Count(m => m.HasData);
            Message = reached < zones.Count
                ? $"{reached} of {zones.Count} corners driven so far; the rest fill in as the lap goes on."
                : "";
        }

        // The selected Racer 1 lap as a trace. For the lap in progress, distance is already on the
        // reference lap's axis (playback maps onto it, and live AC follows the same track).
        private LapTrace? LoadRacer1(double referenceLength)
        {
            string? choice = _selectedRacer1;
            if (choice == null || choice == CurrentLapOption) return _currentLap(referenceLength);

            if (choice.StartsWith(CapturedPrefix, StringComparison.Ordinal))
            {
                RecordedLap? lap = _main.RecordedLaps.FirstOrDefault(l => CapturedLabel(l) == choice);
                return lap == null ? null : LapTrace.FromLap(LapNormalizer.Normalize(lap.Samples));
            }

            if (choice.StartsWith(SavedPrefix, StringComparison.Ordinal))
            {
                return LapTrace.FromLap(_main.LoadSavedLap(choice[SavedPrefix.Length..]));
            }

            return null;
        }

        private void SetEmpty(string message)
        {
            Rows.Clear();
            Summary = "";
            Message = message;
        }
    }
}
