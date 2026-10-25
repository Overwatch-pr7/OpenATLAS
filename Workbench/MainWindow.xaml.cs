using System;
using System.ComponentModel;
using System.Windows;
using OpenATLAS.Workbench.Plotting;
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

            RefreshPlots();
        }

        /// <summary>
        /// Redraws the reference traces when the user picks a different Year/Track/Driver.
        /// </summary>
        private void OnReferenceLapChanged()
        {
            if (_viewModel.ReferenceLap == null) return;

            _tracePanes.ClearLive();
            _tracePanes.LoadReference(_viewModel.ReferenceLap);
            RefreshPlots();
        }

        private void RefreshPlots()
        {
            SpeedPlot.Refresh();
            PedalPlot.Refresh();
            DeltaPlot.Refresh();
        }

        /// <summary>
        /// Cleanly shuts down active live shared memory listeners, background worker threads,
        /// and simulation tasks when the main window is closed by the user.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            _viewModel.ReferenceLapChanged -= OnReferenceLapChanged;
            _viewModel.Dispose();
        }
    }
}