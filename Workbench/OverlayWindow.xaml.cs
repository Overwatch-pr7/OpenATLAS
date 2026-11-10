using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// Floating widget shown over the game: live delta, speed vs reference, gear, lap time and REC
    /// status, with START/STOP, REC and SAVE LAP. It stays on top, never takes focus from the game
    /// (clicks go through without activating it), and is hidden from the taskbar and Alt+Tab.
    /// Assetto Corsa has to run in borderless / windowed mode for any window to appear over it.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            NativeMethods.MakeNonActivatingToolWindow(new WindowInteropHelper(this).Handle);
        }

        // Drag by the header row. DragMove works without activating the window.
        private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnHide(object sender, RoutedEventArgs e) => Hide();
    }
}
