using System;
using System.Runtime.InteropServices;

namespace OpenATLAS.Workbench
{
    /// <summary>
    /// Win32 calls used by the floating widget (focus-free window) and the global hotkeys
    /// that work while the game has focus.
    /// </summary>
    internal static class NativeMethods
    {
        // ---- Extended window styles
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000; // clicks don't take focus from the game
        private const long WS_EX_TOOLWINDOW = 0x00000080;  // not shown in the taskbar or Alt+Tab

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        public static void MakeNonActivatingToolWindow(IntPtr hwnd)
        {
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        // ---- Global hotkeys
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000; // holding the keys down fires once

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
