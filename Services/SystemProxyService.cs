using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XrayUI.Services
{
    // 设置 / 清除 Windows 系统代理（WinInet / IE 代理，浏览器及大多数应用遵循此设置）。
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

        /// <summary>启用系统代理，将 HTTP/HTTPS 流量指向 host:port。</summary>
        public static void SetProxy(string host, int port)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                             ?? throw new InvalidOperationException("无法打开注册表项：" + RegPath);

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
                // 本地地址绕过代理
                key.SetValue("ProxyOverride",
                    "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;" +
                    "172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;" +
                    "172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>",
                    RegistryValueKind.String);
                key.Flush();

                NotifyWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemProxy] SetProxy 失败: {ex.Message}");
            }
        }

        /// <summary>关闭系统代理。</summary>
        public static void ClearProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
                if (key == null) return;

                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
                key.Flush();
                NotifyWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemProxy] ClearProxy 失败: {ex.Message}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>通知 Windows 代理设置已变更，立即生效。</summary>
        private static void NotifyWindows()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH,          IntPtr.Zero, 0);
        }
    }
}
