using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;
namespace XrayUI
{
    public partial class App
    {
        private const string SingleInstanceKey = "XrayUI.MainInstance";
        private const string ParentPidArgumentPrefix = "--parent-pid=";
        private const string TunArgument = "--tun";
        private const string JumpElevatedForwardArgument = "--jump-elevated-forward";
        private const uint ShutdownNoRetry = 0x00000001;
        private const uint ShutdownLevel = 0x280;
        private Window? _window;
        private AppInstance? _mainInstance;
        private bool _cleanupStarted;
        private volatile bool _pendingExternalActivation;
        private readonly ConcurrentQueue<(JumpListRequest Request, bool UseDefaultMode)> _pendingConnections = new();
        private bool _processingConnections;
        private Task<bool> _takeover = Task.FromResult(true);

        public Window? Window => _window;

        public App()
        {
            // Must run before InitializeComponent — the XAML resource loader caches the
            // current locale at first touch, and that happens during component init.
            ApplyPersistedLanguageOverride();

            this.InitializeComponent();
			ConfigureProcessShutdownBehavior();
            this.UnhandledException += (_, _) => CleanupOnExit();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOnExit();
        }

        private static void ApplyPersistedLanguageOverride()
        {
            string? language = null;
            try
            {
                if (File.Exists(AppPaths.SettingsJsonPath))
                {
                    using var stream = File.OpenRead(AppPaths.SettingsJsonPath);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("Language", out var langElem)
                        && langElem.ValueKind == JsonValueKind.String)
                    {
                        language = langElem.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Language] Failed to load persisted language: {ex.Message}");
            }

            LanguageHelper.ApplyOverride(language);
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            var jumpRequest = JumpListRequest.Parse(cmdArgs);
            var parentPid = TryGetParentProcessId(cmdArgs);
            var startMinimized = jumpRequest is null && cmdArgs.Contains(StartupService.StartupMinimizedArgument, StringComparer.OrdinalIgnoreCase);
            var isTunLaunch = cmdArgs.Contains(TunArgument, StringComparer.OrdinalIgnoreCase);
            var isTunTakeover = isTunLaunch && parentPid.HasValue;
            var launchKind = startMinimized ? LaunchKind.Boot
                : jumpRequest is null && !isTunLaunch && !parentPid.HasValue ? LaunchKind.ManualOpen
                : LaunchKind.Other;

            if (!isTunTakeover && await TryRedirectToExistingInstanceAsync(startMinimized, jumpRequest))
            {
                return;
            }

            _window = new MainWindow(launchKind);
            _window.Closed += (_, _) => CleanupOnExit();
            if (jumpRequest is not null)
                _pendingConnections.Enqueue((jumpRequest, !isTunLaunch));

            // Check for --tun: after restarting as administrator, enable TUN mode automatically.
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

            if (parentPid.HasValue)
            {
                _takeover = TakeOverPreviousInstanceAsync(parentPid.Value, isTunTakeover);
            }

            _window.Activate();

            if (startMinimized)
            {
                _window.AppWindow.IsShownInSwitchers = false;
                _window.AppWindow.Hide();
            }

            if (_pendingExternalActivation && _window is MainWindow mainWindow)
            {
                _pendingExternalActivation = false;
                mainWindow.RestoreFromTray();
            }
            _ = ProcessPendingConnectionsAsync();
        }

        public void RequestShutdown(bool fastShutdown = false)
        {
            CleanupOnExit(fastShutdown);
            Environment.Exit(0);
        }

        /// <summary>
        /// Restart the process. xray.exe is a child process not bound to a job object, so
        /// bare termination would leak it (and the system proxy) — and <see cref="AppInstance.Restart"/>
        /// itself bypasses Window.Closed / ProcessExit, so cleanup must be triggered explicitly here.
        /// </summary>
        public static void Restart()
        {
            (Application.Current as App)?.CleanupOnExit(fastShutdown: true);

            // Restart terminates the process on success; any synchronous return (or
            // exception) means the platform refused — fall through to a manual relaunch.
            try { AppInstance.Restart(string.Empty); } catch { }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                // Let an external process wait for this one to exit before relaunching.
                // An in-process delayed task would be killed by Environment.Exit below.
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                        Arguments = $"/c ping 127.0.0.1 -n 2 > nul & start \"\" \"{exePath}\"",
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                }
                catch
                {
                    // Worst case the user relaunches manually.
                }
            }

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

            DeleteConfigPreview();
            SystemProxyService.ClearProxy();

            if (_window is MainWindow mainWindow)
            {
                mainWindow.StopBackgroundServicesOnExit(fastShutdown);
            }
        }

        private static void DeleteConfigPreview()
        {
            try
            {
                // File.Delete is a no-op when no preview has been generated. Keep cleanup
                // best-effort so a JSON editor temporarily locking the file cannot block exit.
                File.Delete(AppPaths.XrayConfigPreviewPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shutdown] Failed to delete config preview: {ex.Message}");
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

        private async Task<bool> TryRedirectToExistingInstanceAsync(bool startMinimized, JumpListRequest? jumpRequest)
        {
            AppInstance mainInstance;
            try
            {
                mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] Failed to register app instance: {ex}");
                if (jumpRequest is null) return false;
                HandleJumpListRedirectFailure(jumpRequest, ex);
                ExitDuplicateInstance();
                return true;
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
                        HandleJumpListRedirectFailure(jumpRequest, error: null);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SingleInstance] Failed to redirect activation: {ex}");
                    HandleJumpListRedirectFailure(jumpRequest, ex);
                }
            }

            ExitDuplicateInstance();
            return true;
        }

        /// <summary>A jump-list click that cannot reach the running instance must not become a
        /// second connection owner: relay it elevated once, or tell the user. No-op for other launches.</summary>
        private static void HandleJumpListRedirectFailure(JumpListRequest? request, Exception? error)
        {
            if (request is null) return;

            if (error?.HResult == unchecked((int)0x80070005) && !AdminHelper.IsAdministrator()
                && !Environment.GetCommandLineArgs().Contains(JumpElevatedForwardArgument, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    // The existing TUN instance may own AppLifecycle's objects at high integrity.
                    // Relay only our node ID, once, through Windows' normal UAC path.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = request.ToArguments() + " " + JumpElevatedForwardArgument,
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                    return;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return; // UAC cancellation leaves the existing connection untouched.
                }
                catch (Exception ex) { Debug.WriteLine($"[JumpList] Elevated relay failed: {ex}"); }
            }

            // No XAML window exists in this short-lived relay. Do not create a second connection owner.
            MessageBoxW(0, L.JumpList_ActivationFailed, L.JumpList_Title, 0x10);
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
            // Redirected activations carry their own arguments, not this process's original command line.
            var arguments = args.Data switch
            {
                Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
                Windows.ApplicationModel.Activation.ICommandLineActivatedEventArgs command => command.Operation.Arguments,
                _ => string.Empty,
            };
            if (JumpListRequest.Parse(CommandLineArguments.Split(arguments)) is { } request)
                _pendingConnections.Enqueue((request, false));
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
                _ = ProcessPendingConnectionsAsync();
            }
            else
            {
                _pendingExternalActivation = true;
            }
        }

        private async Task ProcessPendingConnectionsAsync()
        {
            if (_processingConnections || _window is not MainWindow mainWindow) return;
            _processingConnections = true;
            try
            {
                await mainWindow.Initialization;
                // The outgoing non-elevated process must finish proxy/route cleanup first.
                if (!await _takeover)
                {
                    _pendingConnections.Clear();
                    await mainWindow.ViewModel.Personalize.Dialogs.ShowErrorAsync(
                        L.JumpList_Title, L.JumpList_ActivationFailed);
                    return;
                }
                while (!_cleanupStarted && _pendingConnections.TryDequeue(out var pending))
                {
                    mainWindow.ShowFullWindow();
                    await mainWindow.ViewModel.ConnectFromJumpListAsync(pending.Request, pending.UseDefaultMode);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[JumpList] Activation failed: {ex}");
            }
            finally { _processingConnections = false; }
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

        /// <returns>True once the previous instance has exited and, when asked, this instance
        /// owns the single-instance key.</returns>
        private async Task<bool> TakeOverPreviousInstanceAsync(int parentPid, bool registerSingleInstanceAfterTakeover)
        {
            var previousInstanceExited = false;
            try
            {
                if (parentPid <= 0 || parentPid == Environment.ProcessId)
                {
                    return false;
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
                        try
                        {
                            previousInstance.Kill(entireProcessTree: true);
                        }
                        catch (InvalidOperationException)
                        {
                            // It exited in the narrow gap between WaitForExit and Kill.
                        }

                        previousInstanceExited = previousInstance.HasExited
                            || previousInstance.WaitForExit(3000);
                    }
                    else
                    {
                        previousInstanceExited = true;
                    }
                }
                else
                {
                    previousInstanceExited = true;
                }
            }
            catch (ArgumentException)
            {
                // The previous instance already exited.
                previousInstanceExited = true;
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

                // Startup registration can race with the outgoing process, which still owns
                // the same global hotkeys until it exits. Retry only after its handles have
                // actually been released; otherwise RegisterHotKey returns
                // ERROR_HOTKEY_ALREADY_REGISTERED and this process stays unbound.
                if (previousInstanceExited && _window is MainWindow mainWindow)
                {
                    mainWindow.RegisterGlobalHotkeysAfterProcessTakeover();
                }
            }

            return previousInstanceExited
                && (!registerSingleInstanceAfterTakeover || _mainInstance is not null);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    }
}
