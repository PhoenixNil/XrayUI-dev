using System;
using System.Runtime.InteropServices;

namespace XrayUI.Helpers
{
    internal static class DpiHelper
    {
        public static double GetWindowScale(IntPtr hwnd)
        {
            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                {
                    var dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0) return dpi / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hWnd);
    }
}
