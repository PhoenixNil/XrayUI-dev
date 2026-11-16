using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XrayUI.Services
{
    // Sets and clears the Windows system proxy (WinInet / IE proxy, honored by browsers and most apps).
    public static class SystemProxyService
    {
        private const string RegPath =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH          = 37;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(
            IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Enables the system proxy and points HTTP/HTTPS traffic to host:port.</summary>
        public static void SetProxy(string host, int port)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                             ?? throw new InvalidOperationException("无法打开注册表项：" + RegPath);

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
                // Bypass proxy for local addresses.
                key.SetValue("ProxyOverride",
                    "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;" +
                    "172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;" +
                    "172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>",
                    RegistryValueKind.String);

                NotifyWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemProxy] SetProxy 失败: {ex.Message}");
            }
        }

        /// <summary>Disables the system proxy.</summary>
        public static void ClearProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
                if (key == null) return;

                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                NotifyWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemProxy] ClearProxy 失败: {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Notifies Windows that proxy settings changed so they take effect immediately.</summary>
        private static void NotifyWindows()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH,          IntPtr.Zero, 0);
        }
    }
}
