using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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

        // Where the left button went down; a release within this many pixels is a click, not a pan
        private Point _mouseDownPosition;
        private const double ClickTolerancePixels = 4;

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

            SpeedPlot.Plot.Axes.Link(PedalPlot, x: true, y: false);
            SpeedPlot.Plot.Axes.Link(DeltaPlot, x: true, y: false);
            PedalPlot.Plot.Axes.Link(SpeedPlot, x: true, y: false);
            PedalPlot.Plot.Axes.Link(DeltaPlot, x: true, y: false);
            DeltaPlot.Plot.Axes.Link(SpeedPlot, x: true, y: false);
            DeltaPlot.Plot.Axes.Link(PedalPlot, x: true, y: false);

            _cursor = new DistanceCursor(_tracePanes, _viewModel);
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

        private void RefreshPlots()
        {
            // Keeps a pinned cursor's live readout current as new samples arrive
            _cursor?.Refresh();
            SpeedPlot.Refresh();
            PedalPlot.Refresh();
            DeltaPlot.Refresh();
        }

        #region Distance Cursor

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            var plot = (WpfPlot)sender;
            if (TryGetDistance(plot, e, out double distance))
            {
                _cursor.Hover(distance);
            }
            else
            {
                _cursor.Leave(); // over the axes or labels, not the data area
            }
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
                _cursor.Click(distance);
                RefreshPlots();
            }
        }

        private void OnPlotMouseLeave(object sender, MouseEventArgs e)
        {
            _cursor.Leave();
            RefreshPlots();
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
            base.OnClosing(e);
            _renderLoop.Dispose();
            _viewModel.ReferenceLapChanged -= OnReferenceLapChanged;
            _viewModel.Dispose();
        }
    }
}