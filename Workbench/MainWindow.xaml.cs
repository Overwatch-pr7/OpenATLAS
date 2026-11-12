using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OpenATLAS.Workbench.Plotting;
using ScottPlot;
using ScottPlot.WPF;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// OpenATLAS Phase 4 - Task 4.1: MainWindow Code-Behind
    /// Interaction logic for the primary trackside workbench desktop shell.
    /// Wires up the WorkbenchViewModel and manages window lifecycle and resource cleanup.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly WorkbenchViewModel _viewModel;
        private TracePanes _tracePanes = null!;
        private LiveRenderLoop _renderLoop = null!;
        private DistanceCursor _cursor = null!;
        private TrackMap _trackMap = null!;

        // Floating widget over the game (created on first use) and the global hotkeys that drive it.
        // Hotkeys are system-wide, so they work while Assetto Corsa has focus (and can be mapped to wheel buttons).
        private OverlayWindow? _overlay;

        // Corner analysis pop-up (one at a time, not modal)
        private CornerAnalysisWindow? _cornerWindow;
        private HwndSource? _hwndSource;
        private const int HotkeyToggleRacer1 = 1, HotkeyToggleRecording = 2, HotkeySaveLastLap = 3, HotkeyToggleOverlay = 4;
        private const uint VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A, VK_F12 = 0x7B;

        // Reused every frame for the pins' lap fractions on the track map
        private readonly List<double> _pinFractions = [];

        // Where the left button went down; a release within this many pixels is a click, not a pan
        private Point _mouseDownPosition;
        private const double ClickTolerancePixels = 4;

        // How close (in screen pixels) the mouse must be to a pin for the cursor to snap onto it
        private const double SnapRadiusPixels = 10;

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate ViewModel and assign as the primary DataContext
            _viewModel = new WorkbenchViewModel();
            DataContext = _viewModel;
            InitializeTracePanes();
        }

        /// <summary>
        /// Constructor allowing an externally injected ViewModel (useful for automated testing and mocking).
        /// </summary>
        /// <param name="viewModel">The pre-configured WorkbenchViewModel.</param>
        public MainWindow(WorkbenchViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;
            InitializeTracePanes();
        }

        /// <summary>
        /// Task 4.2: Builds the three ScottPlot trace panes, draws the reference lap,
        /// and links their distance (X) axes so pan/zoom stays synchronized.
        /// </summary>
        private void InitializeTracePanes()
        {
            _tracePanes = new TracePanes(SpeedPlot.Plot, PedalPlot.Plot, DeltaPlot.Plot, _viewModel.BufferCapacity);

            if (_viewModel.ReferenceLap != null)
            {
                _tracePanes.LoadReference(_viewModel.ReferenceLap);
            }

            _viewModel.ReferenceLapChanged += OnReferenceLapChanged;
            _viewModel.LapReset += OnLapReset;

            SpeedPlot.Plot.Axes.Link(PedalPlot, x: true, y: false);
            SpeedPlot.Plot.Axes.Link(DeltaPlot, x: true, y: false);
            PedalPlot.Plot.Axes.Link(SpeedPlot, x: true, y: false);
            PedalPlot.Plot.Axes.Link(DeltaPlot, x: true, y: false);
            DeltaPlot.Plot.Axes.Link(SpeedPlot, x: true, y: false);
            DeltaPlot.Plot.Axes.Link(PedalPlot, x: true, y: false);

            _cursor = new DistanceCursor(_tracePanes, _viewModel);

            _trackMap = new TrackMap(TrackMapPlot.Plot);
            _trackMap.Load(_viewModel.TrackLayout);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            foreach (WpfPlot plot in new[] { SpeedPlot, PedalPlot, DeltaPlot })
            {
                // Preview events so ScottPlot's own pan/zoom handling still runs
                plot.PreviewMouseMove += OnPlotMouseMove;
                plot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
                plot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
                plot.MouseLeave += OnPlotMouseLeave;
            }

            RefreshPlots();

            // Task 4.3: the only place live data reaches the charts, at a fixed 60 Hz
            _renderLoop = new LiveRenderLoop(_viewModel.CircularBuffer, _tracePanes, _viewModel, RefreshPlots);
            _renderLoop.Start();
        }

        /// <summary>
        /// Redraws the reference traces when the user picks a different Year/Track/Driver.
        /// </summary>
        private void OnReferenceLapChanged()
        {
            if (_viewModel.ReferenceLap == null) return;

            _tracePanes.ClearLive();
            _tracePanes.LoadReference(_viewModel.ReferenceLap);
            _cursor.Clear(); // distances belong to the previous track
            RefreshPlots();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The widget's buttons never get focus or input, so WPF wouldn't re-check whether they
            // are enabled on its own; nudge it when the state behind them changes
            if (e.PropertyName is nameof(WorkbenchViewModel.IsRunning) or nameof(WorkbenchViewModel.HasRecordedLaps)
                or nameof(WorkbenchViewModel.IsRecordingLaps) or nameof(WorkbenchViewModel.RecordButtonText))
            {
                CommandManager.InvalidateRequerySuggested();
            }

            if (e.PropertyName == nameof(WorkbenchViewModel.TrackLayout))
            {
                _trackMap.Load(_viewModel.TrackLayout); // a different Grand Prix was selected
                RefreshPlots();
            }
            else if (e.PropertyName == nameof(WorkbenchViewModel.IsTrackMapVisible))
            {
                RefreshPlots(); // the map was not redrawn while hidden
            }
        }

        private void OnLapReset()
        {
            _cursor.Clear(); // pins belong to the lap that was just reset
            RefreshPlots();
        }

        private void RefreshPlots()
        {
            // Keeps a pinned cursor's live readout current as new samples arrive
            _cursor?.Refresh();
            SpeedPlot.Refresh();
            PedalPlot.Refresh();
            DeltaPlot.Refresh();
            RefreshTrackMap();
        }

        /// <summary>
        /// Moves the car, cursor and pin markers on the track map. Positions are lap fractions
        /// (distance / reference lap length), the same scale the map's outline is drawn on.
        /// </summary>
        private void RefreshTrackMap()
        {
            if (_trackMap == null || !_trackMap.HasMap || !_viewModel.IsTrackMapVisible) return;

            double length = _viewModel.ReferenceLap?.TrackLengthMeters ?? 0;
            if (length <= 0) return;

            _trackMap.SetCar(_tracePanes.LatestLiveDistance / length);
            _trackMap.SetHover(_cursor.HoverDistance / length);

            _pinFractions.Clear();
            foreach (double pin in _cursor.Pins) _pinFractions.Add(pin / length);
            _trackMap.SetPins(_pinFractions);

            TrackMapPlot.Refresh();
        }

        #region Floating Widget & Global Hotkeys

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Ctrl+K: corner analysis (opens a window, so it lives here rather than in the view model)
            InputBindings.Add(new KeyBinding(new RelayCommand(ShowCornerAnalysis), Key.K, ModifierKeys.Control));

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource.AddHook(OnWindowMessage);

            const uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
            var failed = new List<string>();
            if (!NativeMethods.RegisterHotKey(hwnd, HotkeyToggleRacer1, mods, VK_F9)) failed.Add("F9");
            if (!NativeMethods.RegisterHotKey(hwnd, HotkeyToggleRecording, mods, VK_F10)) failed.Add("F10");
            if (!NativeMethods.RegisterHotKey(hwnd, HotkeySaveLastLap, mods, VK_F11)) failed.Add("F11");
            if (!NativeMethods.RegisterHotKey(hwnd, HotkeyToggleOverlay, mods, VK_F12)) failed.Add("F12");

            if (failed.Count > 0)
            {
                // Another program already owns these combinations
                _viewModel.StatusMessage = $"Hotkeys in use by another program: Ctrl+Shift+{string.Join(", ", failed)}.";
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

            switch (wParam.ToInt32())
            {
                case HotkeyToggleRacer1: _viewModel.ToggleRacer1(); break;
                case HotkeyToggleRecording: _viewModel.IsRecordingLaps = !_viewModel.IsRecordingLaps; break;
                case HotkeySaveLastLap: _viewModel.SaveLastRecordedLap(); break;
                case HotkeyToggleOverlay: ToggleOverlay(); break;
                default: return IntPtr.Zero;
            }

            handled = true;
            return IntPtr.Zero;
        }

        // View > Floating Widget
        private void OnToggleOverlay(object sender, RoutedEventArgs e) => ToggleOverlay();

        private void ToggleOverlay()
        {
            // Not owned by this window on purpose: an owned window would hide when the
            // main window is minimised, which is exactly when the widget is needed
            _overlay ??= new OverlayWindow { DataContext = _viewModel, Left = 40, Top = 40 };

            if (_overlay.IsVisible) _overlay.Hide();
            else _overlay.Show();
        }

        // SECTOR ANALYSIS > CORNERS, View > Corner Analysis (Ctrl+K)
        private void OnShowCornerAnalysis(object sender, RoutedEventArgs e) => ShowCornerAnalysis();

        private void ShowCornerAnalysis()
        {
            if (_cornerWindow is { IsLoaded: true })
            {
                _cornerWindow.Activate();
                return;
            }

            _cornerWindow = new CornerAnalysisWindow(_viewModel, length => _tracePanes.CopyLiveLap(length)) { Owner = this };
            _cornerWindow.Closed += (_, _) => _cornerWindow = null;
            _cornerWindow.Show();
        }

        #endregion

        #region Menu Bar

        // File > Import Lap from CSV (Ctrl+O)
        private void OnImportLap(object sender, ExecutedRoutedEventArgs e)
        {
            var dialog = new ImportLapWindow(new ImportLapViewModel(_viewModel)) { Owner = this };
            dialog.ShowDialog();
        }

        // File > Save Recorded Laps (Ctrl+S): available while captured laps are waiting
        private void OnSaveRecordedLaps(object sender, ExecutedRoutedEventArgs e)
        {
            new SaveLapsWindow(_viewModel) { Owner = this }.ShowDialog();
        }

        private void CanSaveRecordedLaps(object sender, CanExecuteRoutedEventArgs e)
        {
            // WPF asks before the constructor has set the view model (during InitializeComponent)
            e.CanExecute = _viewModel is { HasRecordedLaps: true };
        }

        // File > Open My Laps Folder
        private void OnOpenMyLapsFolder(object sender, RoutedEventArgs e)
        {
            string? folder = _viewModel.UserLapsFolder;
            if (folder == null)
            {
                _viewModel.StatusMessage = "No 'references' folder was found, so there is no My Laps folder.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void OnExit(object sender, RoutedEventArgs e) => Close();

        // Help > Import File Format
        private void OnShowImportFormat(object sender, RoutedEventArgs e)
        {
            new InfoWindow("Import File Format", ImportLapViewModel.FormatDescription) { Owner = this }.ShowDialog();
        }

        // Help > About
        private void OnAbout(object sender, RoutedEventArgs e)
        {
            const string about =
                "OpenATLAS - Trackside Telemetry & Delta Analysis Workbench\n\n" +
                "Compares two laps distance by distance: Racer 1 (live Assetto Corsa, the built-in\n" +
                "simulation, or one of your saved laps) against Racer 2 (a real Formula 1 lap from\n" +
                "FastF1, or another of your laps).\n\n" +
                "F1 timing data via FastF1. Licensed under the GNU General Public License v3.0.";
            new InfoWindow("About OpenATLAS", about) { Owner = this }.ShowDialog();
        }

        #endregion

        #region Distance Cursor

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            var plot = (WpfPlot)sender;
            if (TryGetDistance(plot, e, out double distance))
            {
                _cursor.Hover(distance, SnapRadiusMeters(plot));
            }
            else
            {
                _cursor.Leave(); // over the axes or labels, not the data area
            }

            // Hand pointer while snapped onto a pin: a click will remove it
            plot.Cursor = _cursor.IsHoverOnPin ? Cursors.Hand : null;
            RefreshPlots();
        }

        private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDownPosition = e.GetPosition((IInputElement)sender);
        }

        private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
        {
            var plot = (WpfPlot)sender;
            if ((e.GetPosition(plot) - _mouseDownPosition).Length > ClickTolerancePixels) return; // that was a pan

            if (TryGetDistance(plot, e, out double distance))
            {
                _cursor.Click(distance, SnapRadiusMeters(plot));
                plot.Cursor = _cursor.IsHoverOnPin ? Cursors.Hand : null;
                RefreshPlots();
            }
        }

        private void OnPlotMouseLeave(object sender, MouseEventArgs e)
        {
            ((WpfPlot)sender).Cursor = null;
            _cursor.Leave();
            RefreshPlots();
        }

        // Snap radius in metres at the current zoom: the plot's visible distance per screen pixel
        private static double SnapRadiusMeters(WpfPlot plot)
        {
            AxisLimits limits = plot.Plot.Axes.GetLimits();
            double dataWidthPixels = plot.Plot.LastRender.DataRect.Width; // physical pixels
            if (dataWidthPixels <= 0) return 0;

            double metersPerPixel = (limits.Right - limits.Left) / dataWidthPixels;
            return SnapRadiusPixels * plot.DisplayScale * metersPerPixel;
        }

        // Converts the mouse position to a track distance; false when outside the plot's data area
        private static bool TryGetDistance(WpfPlot plot, MouseEventArgs e, out double distance)
        {
            Point position = e.GetPosition(plot);
            var pixel = new Pixel((float)(position.X * plot.DisplayScale), (float)(position.Y * plot.DisplayScale));

            distance = plot.Plot.GetCoordinates(pixel).X;
            return plot.Plot.LastRender.DataRect.Contains(pixel);
        }

        #endregion

        /// <summary>
        /// Cleanly shuts down active live shared memory listeners, background worker threads,
        /// and simulation tasks when the main window is closed by the user.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected override void OnClosing(CancelEventArgs e)
        {
            // Captured laps only exist in memory until saved
            if (_viewModel.HasRecordedLaps)
            {
                var answer = MessageBox.Show(this,
                    $"{_viewModel.RecordedLaps.Count} recorded lap(s) have not been saved.\n\nSave them before closing?",
                    "Unsaved Laps", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

                if (answer == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (answer == MessageBoxResult.Yes)
                {
                    new SaveLapsWindow(_viewModel) { Owner = this }.ShowDialog();
                    if (_viewModel.HasRecordedLaps)
                    {
                        e.Cancel = true; // still unsaved laps: the user closed the dialog without saving
                        return;
                    }
                }
            }

            base.OnClosing(e);

            if (_hwndSource != null)
            {
                foreach (int id in new[] { HotkeyToggleRacer1, HotkeyToggleRecording, HotkeySaveLastLap, HotkeyToggleOverlay })
                {
                    NativeMethods.UnregisterHotKey(_hwndSource.Handle, id);
                }
                _hwndSource.RemoveHook(OnWindowMessage);
            }
            _overlay?.Close();
            _renderLoop.Dispose();
            _viewModel.ReferenceLapChanged -= OnReferenceLapChanged;
            _viewModel.LapReset -= OnLapReset;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }
    }
}