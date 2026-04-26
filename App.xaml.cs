using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using XrayUI.Services;

namespace XrayUI
{
    public partial class App
    {
        private const string SingleInstanceKey = "XrayUI.MainInstance";
        private const string ParentPidArgumentPrefix = "--parent-pid=";
        private const string TunArgument = "--tun";
        private const uint ShutdownNoRetry = 0x00000001;
        private const uint ShutdownLevel = 0x280;
        private Window? _window;
        private AppInstance? _mainInstance;
        private bool _cleanupStarted;
        private volatile bool _pendingExternalActivation;

        public Window? Window => _window;

        public App()
        {
            this.InitializeComponent();

            ConfigureProcessShutdownBehavior();
            this.UnhandledException += (_, _) => CleanupOnExit();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOnExit();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            var parentPid = TryGetParentProcessId(cmdArgs);
            var startMinimized = cmdArgs.Contains(StartupService.StartupMinimizedArgument, StringComparer.OrdinalIgnoreCase);
            var isTunLaunch = cmdArgs.Contains(TunArgument, StringComparer.OrdinalIgnoreCase);
            var isTunTakeover = isTunLaunch && parentPid.HasValue;

            if (!isTunTakeover && await TryRedirectToExistingInstanceAsync(startMinimized))
            {
                return;
            }

            _window = new MainWindow(startMinimized);
            _window.Closed += (_, _) => CleanupOnExit();

            // 检测 --tun 参数：以管理员身份重启后自动开启 TUN 模式
            if (isTunLaunch)
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
                _ = TakeOverPreviousInstanceAsync(parentPid.Value, isTunTakeover);
            }

            if (_pendingExternalActivation && _window is MainWindow mainWindow)
            {
                _pendingExternalActivation = false;
                mainWindow.RestoreFromTray();
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

        private async Task<bool> TryRedirectToExistingInstanceAsync(bool startMinimized)
        {
            AppInstance mainInstance;
            try
            {
                mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] Failed to register app instance: {ex}");
                return false;
            }

            if (mainInstance.IsCurrent)
            {
                RegisterCurrentAsMainInstance(mainInstance);
                return false;
            }

            if (!startMinimized)
            {
                try
                {
                    var activatedEventArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                    if (activatedEventArgs is not null)
                    {
                        await mainInstance.RedirectActivationToAsync(activatedEventArgs);
                    }
                    else
                    {
                        Debug.WriteLine("[SingleInstance] Activation args were null; exiting duplicate instance without redirect.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SingleInstance] Failed to redirect activation: {ex}");
                }
            }

            ExitDuplicateInstance();
            return true;
        }

        private bool RegisterCurrentAsMainInstance(AppInstance? appInstance = null)
        {
            try
            {
                appInstance ??= AppInstance.FindOrRegisterForKey(SingleInstanceKey);
                if (!appInstance.IsCurrent)
                {
                    Debug.WriteLine($"[SingleInstance] Instance key is still owned by process {appInstance.ProcessId}.");
                    return false;
                }

                if (_mainInstance is null)
                {
                    _mainInstance = appInstance;
                    _mainInstance.Activated += OnAppInstanceActivated;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] Failed to register current instance: {ex}");
                return false;
            }
        }

        private async Task RegisterCurrentAsMainInstanceAfterTakeoverAsync()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (RegisterCurrentAsMainInstance())
                {
                    return;
                }

                await Task.Delay(150);
            }
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            var window = _window;
            if (window is not null && window.DispatcherQueue.TryEnqueue(RestoreOrDeferActivation))
            {
                return;
            }

            _pendingExternalActivation = true;
        }

        private void RestoreOrDeferActivation()
        {
            if (_window is MainWindow mainWindow)
            {
                mainWindow.RestoreFromTray();
            }
            else
            {
                _pendingExternalActivation = true;
            }
        }

        private static void ExitDuplicateInstance()
        {
            try
            {
                Process.GetCurrentProcess().Kill();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] Failed to terminate duplicate process cleanly: {ex}");
                Environment.FailFast("Duplicate XrayUI instance could not exit without running shutdown cleanup.", ex);
            }
        }

        private async Task TakeOverPreviousInstanceAsync(int parentPid, bool registerSingleInstanceAfterTakeover)
        {
            try
            {
                if (parentPid <= 0 || parentPid == Environment.ProcessId)
                {
                    return;
                }

                await Task.Delay(150);

                using var previousInstance = Process.GetProcessById(parentPid);
                if (!previousInstance.HasExited)
                {
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
            }
            catch (ArgumentException)
            {
                // The previous instance already exited.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] Failed to take over previous instance {parentPid}: {ex}");
            }
            finally
            {
                if (registerSingleInstanceAfterTakeover)
                {
                    await RegisterCurrentAsMainInstanceAfterTakeoverAsync();
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);
    }
}
