using System.Windows;

namespace OpenATLAS.Workbench
{
    /// <summary>A small read-only text window (Help menu).</summary>
    public partial class InfoWindow : Window
    {
        public InfoWindow(string title, string body)
        {
            InitializeComponent();
            Title = title;
            Heading.Text = title.ToUpperInvariant();
            Body.Text = body;
        }
    }
}
