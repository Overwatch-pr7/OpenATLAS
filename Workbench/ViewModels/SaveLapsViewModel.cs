using System;
using System.IO;
using System.Linq;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// File > Save Recorded Laps: choose one of the captured live AC laps, name it and save it to
    /// My Laps, or save them all with their default names. Saved laps leave the waiting list.
    /// </summary>
    public class SaveLapsViewModel : ViewModelBase
    {
        private readonly WorkbenchViewModel _workbench;
        private RecordedLap? _selectedLap;
        private string _lapName = "";
        private string _message = "";

        public SaveLapsViewModel(WorkbenchViewModel workbench)
        {
            _workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
            SelectBest();
        }

        public System.Collections.ObjectModel.ObservableCollection<RecordedLap> Laps => _workbench.RecordedLaps;

        public RecordedLap? SelectedLap
        {
            get => _selectedLap;
            set
            {
                if (!SetProperty(ref _selectedLap, value)) return;
                LapName = value?.DefaultName ?? "";
                OnPropertyChanged(nameof(CanSave));
            }
        }

        public string LapName
        {
            get => _lapName;
            set
            {
                if (SetProperty(ref _lapName, value)) OnPropertyChanged(nameof(CanSave));
            }
        }

        /// <summary>Result of the last save (or why it failed).</summary>
        public string Message { get => _message; private set => SetProperty(ref _message, value); }

        public bool CanSave => SelectedLap != null && !string.IsNullOrWhiteSpace(LapName);

        public bool CanSaveAll => Laps.Count > 0;

        /// <summary>Saves the selected lap, then moves on to the fastest lap still waiting.</summary>
        public string? Save()
        {
            if (!CanSave) return null;
            try
            {
                string saved = _workbench.SaveRecordedLap(SelectedLap!, LapName);
                Message = $"Saved \"{saved}\".";
                SelectBest();
                return saved;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                Message = ex.Message;
                return null;
            }
        }

        /// <summary>Saves every waiting lap under its default name. Returns how many were saved.</summary>
        public int SaveAll()
        {
            int saved = 0;
            foreach (RecordedLap lap in Laps.ToList())
            {
                try
                {
                    _workbench.SaveRecordedLap(lap, lap.DefaultName);
                    saved++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
                {
                    Message = $"Lap {lap.Number}: {ex.Message}";
                }
            }

            if (saved > 0 && Laps.Count == 0) Message = $"Saved {saved} lap(s).";
            SelectBest();
            return saved;
        }

        private void SelectBest()
        {
            SelectedLap = Laps.MinBy(l => l.LapTime);
            OnPropertyChanged(nameof(CanSaveAll));
        }
    }
}
