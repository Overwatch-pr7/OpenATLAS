using System.Windows;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// File > Save Recorded Laps. Saving logic lives in <see cref="SaveLapsViewModel"/>;
    /// this window closes itself once no laps are left waiting.
    /// </summary>
    public partial class SaveLapsWindow : Window
    {
        private readonly SaveLapsViewModel _viewModel;
        private readonly WorkbenchViewModel _workbench;

        public SaveLapsWindow(WorkbenchViewModel workbench)
        {
            InitializeComponent();
            _workbench = workbench;
            _viewModel = new SaveLapsViewModel(workbench);
            DataContext = _viewModel;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_viewModel.Save() != null && !_workbench.HasRecordedLaps) Close();
        }

        private void OnSaveAll(object sender, RoutedEventArgs e)
        {
            _viewModel.SaveAll();
            if (!_workbench.HasRecordedLaps) Close();
        }

        private void OnDiscardAll(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(this,
                $"Discard {_workbench.RecordedLaps.Count} unsaved lap(s)? This cannot be undone.",
                "Discard Recorded Laps", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            _workbench.DiscardRecordedLaps();
            Close();
        }
    }
}
