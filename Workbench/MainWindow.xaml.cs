using System;
using System.ComponentModel;
using System.Windows;
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

        public MainWindow()
        {
            InitializeComponent();

            // Instantiate ViewModel and assign as the primary DataContext
            _viewModel = new WorkbenchViewModel();
            DataContext = _viewModel;
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
        }

        /// <summary>
        /// Cleanly shuts down active live shared memory listeners, background worker threads,
        /// and simulation tasks when the main window is closed by the user.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            _viewModel.Dispose();
        }
    }
}