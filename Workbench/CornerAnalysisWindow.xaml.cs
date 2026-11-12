using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using OpenATLAS.Core.Analysis;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// Task 5.2: corner analysis pop-up (SECTOR ANALYSIS > CORNERS, or View > Corner Analysis).
    /// Not modal, so it can stay open beside the graphs. While it follows the lap in progress it
    /// refreshes once a second; it also picks up newly captured or saved laps and Racer 2 changes.
    /// </summary>
    public partial class CornerAnalysisWindow : Window
    {
        private readonly WorkbenchViewModel _main;
        private readonly CornerAnalysisViewModel _viewModel;
        private readonly DispatcherTimer _liveTimer;

        public CornerAnalysisWindow(WorkbenchViewModel main, Func<double, LapTrace?> currentLap)
        {
            InitializeComponent();
            _main = main;
            _viewModel = new CornerAnalysisViewModel(main, currentLap);
            DataContext = _viewModel;

            _main.ReferenceLapChanged += OnLapsChanged;
            _main.RecordedLaps.CollectionChanged += OnRecordedLapsChanged;
            _main.PropertyChanged += OnMainPropertyChanged;

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += OnLiveTick;
            _liveTimer.Start();

            Closed += OnClosed;
        }

        private void OnLiveTick(object? sender, EventArgs e)
        {
            if (_viewModel.IsCurrentLapSelected && _main.IsRunning) _viewModel.Refresh();
        }

        private void OnLapsChanged() => _viewModel.RefreshOptions();

        private void OnRecordedLapsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _viewModel.RefreshOptions();

        private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(WorkbenchViewModel.AvailableSavedLaps) or nameof(WorkbenchViewModel.TrackLayout))
            {
                _viewModel.RefreshOptions();
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnClosed(object? sender, EventArgs e)
        {
            _liveTimer.Stop();
            _main.ReferenceLapChanged -= OnLapsChanged;
            _main.RecordedLaps.CollectionChanged -= OnRecordedLapsChanged;
            _main.PropertyChanged -= OnMainPropertyChanged;
        }
    }
}
