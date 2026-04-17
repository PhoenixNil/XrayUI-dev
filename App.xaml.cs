using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using XrayUI.Services;

namespace XrayUI
{
    public partial class App
    {
        private const string ParentPidArgumentPrefix = "--parent-pid=";
        private Window? _window;
        private bool _cleanupStarted;

        public App()
        {
            this.InitializeComponent();

            this.UnhandledException += (_, _) => CleanupOnExit();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOnExit();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            var parentPid = TryGetParentProcessId(cmdArgs);

            _window = new MainWindow();
            _window.Closed += (_, _) => CleanupOnExit();

            // 检测 --tun 参数：以管理员身份重启后自动开启 TUN 模式
            if (cmdArgs.Contains("--tun", StringComparer.OrdinalIgnoreCase))
            {
                if (_window is MainWindow mw)
                    mw.ViewModel.ControlPanel.SetTunEnabledSilently(true);
            }

            _window.Activate();

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
            CleanupOnExit();
        }

        private void CleanupOnExit()
        {
            if (_cleanupStarted)
            {
                return;
            }

            _cleanupStarted = true;

            SystemProxyService.ClearProxy();

            if (_window is MainWindow mainWindow)
            {
                mainWindow.StopBackgroundServicesOnExit();
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
    }
}
