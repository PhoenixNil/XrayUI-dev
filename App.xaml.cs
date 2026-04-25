using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using XrayUI.Services;

namespace XrayUI
{
    public partial class App
    {
        private const string ParentPidArgumentPrefix = "--parent-pid=";
        private const uint ShutdownNoRetry = 0x00000001;
        private const uint ShutdownLevel = 0x280;
        private Window? _window;
        private bool _cleanupStarted;

        public Window? Window => _window;

        public App()
        {
            this.InitializeComponent();

            ConfigureProcessShutdownBehavior();
            this.UnhandledException += (_, _) => CleanupOnExit();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOnExit();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            var parentPid = TryGetParentProcessId(cmdArgs);
            var startMinimized = cmdArgs.Contains(StartupService.StartupMinimizedArgument, StringComparer.OrdinalIgnoreCase);

            _window = new MainWindow(startMinimized);
            _window.Closed += (_, _) => CleanupOnExit();

            // 检测 --tun 参数：以管理员身份重启后自动开启 TUN 模式
            if (cmdArgs.Contains("--tun", StringComparer.OrdinalIgnoreCase))
            {
                if (_window is MainWindow mw)
                    mw.ViewModel.ControlPanel.SetTunEnabledSilently(true);
            }

            // Park the window off-screen before Activate() so the brief window
            // of visibility between Activate and the first Hide is invisible
            // to the user — synchronous Hide alone isn't enough because DWM
            // composes frames on its own thread. MainWindow centers the window
            // the first time the user opens it from the tray.
            if (startMinimized)
            {
                _window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            }

            _window.Activate();

            if (startMinimized)
            {
                _window.AppWindow.IsShownInSwitchers = false;
                _window.AppWindow.Hide();
            }

            if (parentPid.HasValue)
            {
                _ = TakeOverPreviousInstanceAsync(parentPid.Value);
            }
        }

        public void RequestShutdown()
        {
            CleanupOnExit();
            Environment.Exit(0);
        }

        public void HandleSessionEnding()
        {
            CleanupOnExit(fastShutdown: true);
        }

        private void CleanupOnExit(bool fastShutdown = false)
        {
            if (_cleanupStarted)
            {
                return;
            }

            _cleanupStarted = true;

            SystemProxyService.ClearProxy();

            if (_window is MainWindow mainWindow)
            {
                mainWindow.StopBackgroundServicesOnExit(fastShutdown);
            }
        }

        private static void ConfigureProcessShutdownBehavior()
        {
            try
            {
                if (!SetProcessShutdownParameters(ShutdownLevel, ShutdownNoRetry))
                {
                    Debug.WriteLine($"[Shutdown] SetProcessShutdownParameters failed: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] Failed to configure shutdown behavior: {ex.Message}");
            }
        }

        private static int? TryGetParentProcessId(string[] cmdArgs)
        {
            foreach (var arg in cmdArgs)
            {
                if (!arg.StartsWith(ParentPidArgumentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = arg[ParentPidArgumentPrefix.Length..];
                if (int.TryParse(value, out var pid) && pid > 0)
                {
                    return pid;
                }
            }

            return null;
        }

        private static async Task TakeOverPreviousInstanceAsync(int parentPid)
        {
            if (parentPid <= 0 || parentPid == Environment.ProcessId)
            {
                return;
            }

            try
            {
                await Task.Delay(150);

                using var previousInstance = Process.GetProcessById(parentPid);
                if (previousInstance.HasExited)
                {
                    return;
                }

                try
                {
                    previousInstance.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // Ignore; some startup states have no main window handle yet.
                }

                if (!previousInstance.WaitForExit(350))
                {
                    previousInstance.Kill(entireProcessTree: true);
                    previousInstance.WaitForExit(3000);
                }
            }
            catch (ArgumentException)
            {
                // The previous instance already exited.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] Failed to take over previous instance {parentPid}: {ex}");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);
    }
}
