using System.Windows;
using Microsoft.Win32;
using OpenATLAS.Workbench.ViewModels;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// File > Import Lap: picks a CSV log and saves one of its laps to My Laps.
    /// All parsing and saving lives in <see cref="ImportLapViewModel"/>; this only handles the dialogs.
    /// </summary>
    public partial class ImportLapWindow : Window
    {
        private readonly ImportLapViewModel _viewModel;

        public ImportLapWindow(ImportLapViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            FormatText.Text = ImportLapViewModel.FormatDescription;
        }

        /// <summary>Name the lap was saved under, once imported.</summary>
        public string? ImportedLapName { get; private set; }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose a telemetry log",
                Filter = "Telemetry logs (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
            };

            if (dialog.ShowDialog(this) == true)
            {
                _viewModel.LoadFile(dialog.FileName);
            }
        }

        private void OnImport(object sender, RoutedEventArgs e)
        {
            ImportedLapName = _viewModel.Import();
            if (ImportedLapName != null) DialogResult = true;
        }
    }
}
