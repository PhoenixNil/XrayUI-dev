using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace XrayUI.Services
{
    public class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "XrayUI";

        public bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return !string.IsNullOrEmpty(key?.GetValue(AppName) as string);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] IsStartupEnabled failed: {ex.Message}");
                return false;
            }
        }

        public void SetStartupEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? throw new InvalidOperationException("Cannot open Run registry key.");

                if (enabled)
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName
                        ?? throw new InvalidOperationException("Cannot resolve exe path.");
                    key.SetValue(AppName, $"\"{exe}\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] SetStartupEnabled({enabled}) failed: {ex.Message}");
            }
        }
    }
}
