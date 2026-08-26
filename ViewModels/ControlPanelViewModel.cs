using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class ControlPanelViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;
        private readonly SettingsService _settings;
        private readonly XrayService _xray;
        private readonly TunService _tunService;
        private readonly ConfigProfileStore _profiles;
        private readonly IUpdateService _update;
        private UpdateInfo? _availableUpdate;
        private IReadOnlyList<string> _availableUpdateNotes = Array.Empty<string>();
        // Guards OnIsTunModeChanged from firing the dialog when we update internally
        private bool _isTunInternalUpdate;

        // Tracks the server host of the currently active TUN session (for cleanup)
        private string? _currentTunServerHost;

        // The local socks/http port the running config actually exposes, captured at start and
        // on every reapply. The proxy-mode toggle re-points the WinInet registry without
        // rebuilding the config, so it cannot read the port back out of a fresh build the way
        // start and reapply do.
        private int? _activeLocalProxyPort;

        // Mirrors AppSettings.Use*ConfigProfile so the gear menu can gate on it synchronously.
        private bool _useTunConfigProfile;
        private bool _useProxyConfigProfile;

        public XrayService XrayService => _xray;
        public SettingsService SettingsService => _settings;

        public Func<ServerEntry?> GetSelectedServer { get; set; } = () => null;

        public Func<IEnumerable<ServerEntry>> GetAllServers { get; set; } = () => Array.Empty<ServerEntry>();

        public Func<bool> CanStartSelectedServer { get; set; } = () => false;

        // Snapshot of the server xray is actually running with, so reapply restarts
        // against the live session rather than whatever is now selected in the list.
        private ServerEntry? _activeServer;
        private string _activeServerName = string.Empty;

        /// <summary>Id of the node xray is running right now, or null when stopped. Read by
        /// PersonalizeViewModel when auto-connect is switched on mid-session, so the boot
        /// target is the node actually in use rather than whatever the list has selected.</summary>
        public string? ActiveServerId => IsRunning ? _activeServer?.Id : null;

        /// <summary>The local socks/http port the running config actually exposes, or null when
        /// xray is stopped or the active config profile publishes no socks/http inbound on a
        /// fixed port. Everything that dials the core in-process reads this — subscription
        /// fetches, the update check and download, the AI-unlock probes — so a profile that
        /// moves or drops the inbound cannot leave them on a dead port. <see cref="LocalPort"/>
        /// stays the *configured* value the port editor edits; this is the *running* one, and
        /// the two only differ under a config profile.</summary>
        public int? ActiveLocalProxyPort => IsRunning ? _activeLocalProxyPort : null;

        // Serializes concurrent reapply calls (custom-rules save, routing-mode toggle,
        // proxy-mode toggle can all race) and blocks re-entry.
        private readonly SemaphoreSlim _reapplyLock = new(1, 1);

        /// <summary>True while ReapplyRoutingAsync is mid-restart. UI uses this to
        /// disable related menu items and show the applying state.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsModeToggleEnabled))]
        [NotifyPropertyChangedFor(nameof(IsBuiltInConfigEnabled))]
        [NotifyPropertyChangedFor(nameof(IsTunToggleEnabled))]
        [NotifyPropertyChangedFor(nameof(IsNotReapplying))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial bool IsReapplying { get; private set; }

        partial void OnIsReapplyingChanged(bool value) => NotifyStartStopStateChanged();

        /// <summary>Inverse of <see cref="IsReapplying"/> for x:Bind IsEnabled targets
        /// (x:Bind doesn't support expression negation).</summary>
        public bool IsNotReapplying => !IsReapplying;

        public bool CanStartStop => !IsReapplying && (IsRunning || CanStartSelectedServer());

        public void NotifyStartStopStateChanged()
        {
            OnPropertyChanged(nameof(CanStartStop));
            StartStopCommand.NotifyCanExecuteChanged();
        }

        public event EventHandler? ShowLogsRequested;
        public event EventHandler? ShowPersonalizeRequested;
        public event EventHandler<CustomRulesViewModel>? ShowCustomRulesRequested;
        /// <summary>Carries the slot to open on alongside the VM: the window has to seed it
        /// before InitializeComponent, so it cannot read the mode off the VM afterwards.</summary>
        public readonly record struct ConfigProfileRequest(ConfigProfileViewModel ViewModel, bool TunSlot);

        public event EventHandler<ConfigProfileRequest>? ShowConfigProfilesRequested;

        public ControlPanelViewModel(
            IDialogService dialogs,
            SettingsService settings,
            XrayService xray,
            TunService tunService,
            IUpdateService update,
            ConfigProfileStore profiles)
        {
            _dialogs        = dialogs;
            _settings       = settings;
            _xray           = xray;
            _tunService     = tunService;
            _update         = update;
            _profiles       = profiles;

            StartStopButtonContent = L.ControlPanel_Start;
            LocalPort              = 16890;
            RoutingMode            = "smart";
            IsSystemProxyEnabled   = true;
        }

        // ── Running state ─────────────────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        public partial string StartStopButtonContent { get; private set; }

        [ObservableProperty]
        public partial bool StartStopButtonChecked { get; private set; }

        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        public string StatusText =>
            IsReapplying ? L.ControlPanel_StatusApplying :
            IsRunning    ? _activeServerName :
                           L.ControlPanel_StatusNotRunning;

        partial void OnIsRunningChanged(bool value)
        {
            StartStopButtonContent = value ? L.ControlPanel_Stop : L.ControlPanel_Start;
            StartStopButtonChecked = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsModeToggleEnabled));
            OnPropertyChanged(nameof(IsBuiltInConfigEnabled));
            OnPropertyChanged(nameof(IsTunToggleEnabled));
            NotifyStartStopStateChanged();
        }

        // ── Start / Stop ──────────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanStartStop))]
        private async Task StartStop()
        {
            if (!CanStartStop) return;

            try
            {
                if (IsRunning)
                {
                    // Serialize with SwitchToSelectedServerAsync and hold IsReapplying:
                    // the netsh cleanup inside the stop path now runs off the UI thread,
                    // so without these gates a switch (double-click / subscription
                    // auto-switch) could interleave with the multi-second stop and
                    // stomp the session state.
                    await _reapplyLock.WaitAsync();
                    try
                    {
                        if (!IsRunning) return;

                        IsReapplying = true;
                        try
                        {
                            await StopCurrentSessionAsync();
                        }
                        finally
                        {
                            IsReapplying = false;
                        }
                    }
                    finally
                    {
                        _reapplyLock.Release();
                    }
                    return;
                }

                await StartSelectedServerAsync();
            }
            catch (Exception ex)
            {
                await HandleStartStopFailureAsync(ex);
            }
        }

        public async Task SwitchToSelectedServerAsync()
        {
            if (!IsRunning) return;
            if (IsReapplying) return;

            var selectedServer = GetSelectedServer();
            if (selectedServer is null || ReferenceEquals(selectedServer, _activeServer))
                return;

            await _reapplyLock.WaitAsync();
            try
            {
                if (!IsRunning) return;

                selectedServer = GetSelectedServer();
                if (selectedServer is null || ReferenceEquals(selectedServer, _activeServer))
                    return;

                IsReapplying = true;
                try
                {
                    await StopCurrentSessionAsync();
                    await StartSelectedServerAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ControlPanel] Switch server failed: {ex}");
                    await HandleStartStopFailureAsync(ex);
                }
                finally
                {
                    IsReapplying = false;
                }
            }
            finally
            {
                _reapplyLock.Release();
            }
        }

        private async Task StopCurrentSessionAsync()
        {
            await CleanupTunStateAsync();
            await _xray.StopAsync();
            if (IsSystemProxyEnabled && !IsTunMode)
                SystemProxyService.ClearProxy();
            _activeServer          = null;
            _activeServerName      = string.Empty;
            _activeLocalProxyPort = null;
            IsRunning = false;
        }

        private async Task<bool> StartSelectedServerAsync()
        {
            var server = GetSelectedServer();
            if (server is null)
            {
                await _dialogs.ShowErrorAsync(L.Error_NoServer, L.Error_NoServerMsg);
                return false;
            }

            // One consistent mode for the whole start sequence: the preflight/cleanup
            // awaits below keep the UI interactive, so the live IsTunMode property can
            // be toggled mid-start and must not drive the post-start bookkeeping.
            var tunMode = IsTunMode;

            var appSettings = await _settings.LoadSettingsAsync();
            ApplyLiveSessionState(appSettings, tunMode);
            // Auto-connect lives in Personalize now; settings are the shared truth and this
            // load is already on the path, so read the flag from there instead of mirroring
            // it onto a second property here.
            if (appSettings.IsAutoConnect)
                appSettings.LastAutoConnectServerId = server.Id;

            if (tunMode)
            {
                if (!await RunTunPreflightAsync()) return false;
                await CleanupPersistedTunRoutesAsync(appSettings);
            }

            var built = await BuildForNextStartAsync(server, appSettings, tunMode);
            var ok = await _xray.StartAsync(built.Json);

            if (!ok)
            {
                var detail = string.IsNullOrEmpty(_xray.LastError)
                    ? L.Error_XrayStartFailed
                    : _xray.LastError;
                await _dialogs.ShowErrorAsync(L.Error_StartFailed, detail);
                return false;
            }

            if (tunMode)
            {
                // xray inherits admin from the parent process (HandleTunToggleAsync restarted
                // the app as admin) and configures the TUN adapter + system routes itself via
                // autoSystemRoutingTable. C# only remembers the active session for cleanup.
                _currentTunServerHost = server.Host;
                appSettings.LastTunServerHost = server.Host;
                await TrySaveSettingsAsync(appSettings, "persist TUN runtime state");
            }
            else
            {
                appSettings.LastTunServerHost    = null;
                appSettings.IsSystemProxyEnabled = IsSystemProxyEnabled;
                ApplySystemProxy(built.SystemProxyPort);
                await TrySaveSettingsAsync(appSettings, "persist system proxy settings");
            }

            _activeServer            = server;
            _activeServerName        = server.Name;
            _activeLocalProxyPort   = built.SystemProxyPort;
            IsRunning = true;


            return true;
        }

        /// <summary>
        /// Builds the config the currently selected node would use on its next start without
        /// starting xray or changing any proxy/TUN state. A serialized clone prevents the live
        /// UI values overlaid for the preview from mutating SettingsService's cached instance.
        /// </summary>
        public async Task<string?> BuildSelectedConfigPreviewAsync(string? routingRegion = null)
        {
            var server = GetSelectedServer();
            if (server is null || !CanStartSelectedServer()) return null;

            var previewSettings = (await _settings.LoadSettingsAsync()).Clone();

            ApplyLiveSessionState(previewSettings);
            previewSettings.IsSystemProxyEnabled = IsSystemProxyEnabled;
            // Routing region belongs to Personalize, not this toolbar. Callers that have a
            // not-yet-saved selection pass it; the rest get the persisted value.
            if (routingRegion is not null)
                previewSettings.RoutingRegion = routingRegion;

            return (await BuildForNextStartAsync(server, previewSettings, previewSettings.IsTunMode)).Json;
        }

        /// <summary>
        /// Overlays the toolbar state that is authoritative for the next xray start onto
        /// <paramref name="settings"/>. These controls are the effective next-start values even
        /// when their persisted counterparts have not caught up yet, so starting, reapplying and
        /// previewing all have to agree on them — a preview that silently diverges from what
        /// start actually builds is the failure the preview exists to prevent.
        ///
        /// IsSystemProxyEnabled is deliberately not here: StartSelectedServerAsync only commits it
        /// on the non-TUN branch, after the build.
        /// </summary>
        /// <param name="tunMode">Pins TUN mode for callers that captured one value for a whole
        /// sequence, rather than reading the live property that the user can toggle mid-start.</param>
        private void ApplyLiveSessionState(AppSettings settings, bool? tunMode = null)
        {
            settings.LocalMixedPort      = LocalPort;
            settings.AllowLanConnections = AllowLanConnections;
            settings.RoutingMode         = RoutingMode;
            settings.IsTunMode           = tunMode ?? IsTunMode;
        }

        /// <summary>
        /// Points the WinInet proxy at the port the built config actually listens on. A config
        /// profile is free to move the mixed inbound or drop it entirely, so the port comes from
        /// the build rather than from AppSettings.LocalMixedPort. A null port means the config
        /// exposes no socks/http inbound to point at: leave the registry alone rather than
        /// advertise a port nothing is listening on.
        /// </summary>
        private void ApplySystemProxy(int? port)
        {
            if (!IsSystemProxyEnabled) return;

            if (port is null)
            {
                Debug.WriteLine(
                    "[ControlPanel] System proxy not set: the config exposes no socks/http inbound on a fixed port.");
                return;
            }

            SystemProxyService.SetProxy("127.0.0.1", port.Value);
        }

        /// <summary>
        /// Builds the config for the next start, honouring whichever config profile governs the
        /// given mode. Start, reapply and preview all go through here so the profile lookup can
        /// never be omitted at one of them — skipping it would quietly build the generated config
        /// while the UI still reported "custom".
        /// </summary>
        /// <param name="tunMode">Pinned by callers that captured one value for a whole sequence,
        /// rather than read from the live toggle mid-start.</param>
        private async Task<BuiltXrayConfig> BuildForNextStartAsync(
            ServerEntry server, AppSettings settings, bool tunMode)
        {
            var profileJson = await _profiles.LoadActiveAsync(settings, tunMode);
            return XrayConfigBuilder.Build(server, settings, GetAllServers(), profileJson);
        }

        private async Task HandleStartStopFailureAsync(Exception ex)
        {
            Debug.WriteLine($"[ControlPanel] Start/stop failed: {ex}");

            if (_xray.IsRunning)
            {
                await _xray.StopAsync();
            }

            SystemProxyService.ClearProxy();
            _activeServer          = null;
            _activeServerName      = string.Empty;
            _activeLocalProxyPort = null;
            IsRunning = false;
            await _dialogs.ShowErrorAsync(L.Error_StartFailed, ex.Message);
        }

        /// <summary>
        /// Rebuild xray config from persisted settings and restart xray. No-op if not running.
        /// Always reapplies against the live _activeServer, not the currently-selected list entry.
        /// Not used in TUN mode: changing DNS/routing there is saved and takes effect
        /// after the user restarts the proxy session.
        /// </summary>
        public async Task ReapplyRoutingAsync()
        {
            if (!IsRunning) return;
            if (_activeServer is null) return;
            if (IsTunMode) return;

            await _reapplyLock.WaitAsync();
            try
            {
                var activeServer = _activeServer;
                if (!IsRunning || activeServer is null) return;

                IsReapplying = true;
                try
                {
                    var settings = await _settings.LoadSettingsAsync();
                    ApplyLiveSessionState(settings);
                    settings.IsSystemProxyEnabled = IsSystemProxyEnabled;

                    var built = await BuildForNextStartAsync(activeServer, settings, settings.IsTunMode);

                    var ok = await _xray.StartAsync(built.Json);
                    if (!ok)
                    {
                        var detail = string.IsNullOrEmpty(_xray.LastError)
                            ? L.Error_XrayReapplyFailed
                            : _xray.LastError;
                        await HandleReapplyFailureAsync(detail);
                        return;
                    }

                    _activeLocalProxyPort = built.SystemProxyPort;
                    ApplySystemProxy(built.SystemProxyPort);
                    // IsRunning is managed manually by this VM (no subscription to
                    // _xray.RunningChanged), and the guard at the top of this method
                    // already proves it's true here — so no reassignment is needed.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ControlPanel] Reapply failed: {ex}");
                    await HandleReapplyFailureAsync(ex.Message);
                }
                finally
                {
                    IsReapplying = false;
                }
            }
            finally
            {
                _reapplyLock.Release();
            }
        }

        /// <summary>
        /// Reapply failed. xray is stopped (StartAsync stops first, then failed).
        /// Clear state, revert UI to not-running, notify user.
        /// Caller is already inside _reapplyLock.
        /// </summary>
        private async Task HandleReapplyFailureAsync(string detail)
        {
            try
            {
                if (_xray.IsRunning) await _xray.StopAsync();
            }
            catch (Exception ex) { Debug.WriteLine($"[ControlPanel] Stop after reapply failure: {ex.Message}"); }

            if (IsTunMode)
            {
                try { await CleanupTunStateAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[ControlPanel] TUN cleanup after reapply failure: {ex.Message}"); }
            }
            else
            {
                SystemProxyService.ClearProxy();
            }

            _activeServer          = null;
            _activeServerName      = string.Empty;
            _activeLocalProxyPort = null;
            IsRunning = false;

            await _dialogs.ShowErrorAsync(L.Error_ReapplyFailed, detail);
        }



        /// <summary>
        /// Runs the shared TUN-mode preflight: wintun availability and system-proxy clearing.
        /// Xray-core handles outbound interface selection through autoOutboundsInterface="auto".
        /// </summary>
        private async Task<bool> RunTunPreflightAsync()
        {
            if (!_tunService.IsWintunAvailable())
            {
                await _dialogs.ShowErrorAsync(L.Tun_PreflightErrorTitle,
                    Loc.Format("Tun_WintunNotFound", _tunService.GetExpectedWintunPath()));
                return false;
            }

            await Task.Run(_tunService.ResetTunDnsServers);
            SystemProxyService.ClearProxy();
            return true;
        }

        /// <summary>
        /// Synchronous on purpose: CleanupTunOnExit (Window.Closed / crash / the elevation
        /// handoff racing its 800ms self-kill) runs it inline; the WM_ENDSESSION fast path
        /// uses CleanupCurrentTunRoutesWithoutElevation instead and never reaches here.
        /// Interactive callers wrap it in Task.Run because the netsh/route batch blocks in
        /// WaitForExit (up to 5s when already elevated; the unelevated runas branch can
        /// additionally block on the UAC prompt).
        /// </summary>
        private void CleanupTunRoutesSafely()
        {
            var serverHost = ResolveTunServerHostForCleanup();
            if (string.IsNullOrWhiteSpace(serverHost)) return;
            try
            {
                _tunService.CleanupTunRoutes(serverHost);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 清理路由失败: {ex.Message}");
            }
            finally
            {
                _currentTunServerHost = null;
            }
        }

        /// <summary>Used by MainWindow.StopBackgroundServicesOnExit to ensure routes are cleaned up on exit.</summary>
        private string? ResolveTunServerHostForCleanup()
        {
            if (!string.IsNullOrWhiteSpace(_currentTunServerHost))
                return _currentTunServerHost;

            try
            {
                return _settings.LoadSettingsAsync().GetAwaiter().GetResult().LastTunServerHost;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 读取持久化 TUN 服务器主机失败: {ex.Message}");
                return null;
            }
        }

        private async Task CleanupPersistedTunRoutesAsync(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.LastTunServerHost))
                return;

            await Task.Run(CleanupTunRoutesSafely);
            settings.LastTunServerHost = null;
            await TrySaveSettingsAsync(settings, "clear persisted TUN routes");
        }

        private async Task CleanupTunStateAsync()
        {
            await Task.Run(CleanupTunRoutesSafely);

            var settings = await _settings.LoadSettingsAsync();
            settings.IsTunMode = false;
            settings.LastTunServerHost = null;
            await TrySaveSettingsAsync(settings, "clear TUN state");
        }

        public void CleanupTunOnExit(bool fastShutdown = false)
        {
            if (fastShutdown)
            {
                CleanupCurrentTunRoutesWithoutElevation();
                return;
            }

            CleanupTunRoutesSafely();

            try
            {
                var settings = _settings.LoadSettingsAsync().GetAwaiter().GetResult();
                settings.IsTunMode = false;
                settings.LastTunServerHost = null;
                TrySaveSettingsAsync(settings, "persist shutdown cleanup").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 退出时保存 TUN 状态失败: {ex.Message}");
            }
        }

        private void CleanupCurrentTunRoutesWithoutElevation()
        {
            if (string.IsNullOrWhiteSpace(_currentTunServerHost))
                return;

            if (!AdminHelper.IsAdministrator())
            {
                _currentTunServerHost = null;
                return;
            }

            try
            {
                _tunService.CleanupTunRoutes(_currentTunServerHost);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 关机快速清理路由失败: {ex.Message}");
            }
            finally
            {
                _currentTunServerHost = null;
            }
        }

        // ── TUN mode toggle ───────────────────────────────────────────────────

        [ObservableProperty]
        public partial bool IsTunMode { get; set; }

        public string TunModeText => IsTunMode ? "On" : "Off";

        /// <summary>
        /// Whether routing mode and proxy mode can be toggled.
        /// Runtime changes automatically reapply settings, but they are blocked while TUN mode is running
        /// to avoid disturbing the TUN pipeline. Toggles are also disabled during reapply to prevent re-entry.
        /// </summary>
        public bool IsModeToggleEnabled => !IsReapplying && !(IsRunning && IsTunMode);

        /// <summary>The TUN toggle itself is disabled while running because changing TUN requires
        /// restarting xray and updating the network stack. It is also disabled during reapply.</summary>
        public bool IsTunToggleEnabled => !IsRunning && !IsReapplying;

        /// <summary>
        /// True when the next start in the current mode runs a hand-written config profile
        /// instead of the generated config. Which slot applies follows the TUN toggle, so this
        /// flips as the user switches modes.
        /// </summary>
        private bool IsCustomConfigActive => IsTunMode ? _useTunConfigProfile : _useProxyConfigProfile;

        /// <summary>Gate for the gear-menu items a config profile takes ownership of: local
        /// port, routing mode, custom rules and DNS all live in the profile once it is on, so
        /// leaving them clickable would let the UI report settings xray never sees.</summary>
        public bool IsBuiltInConfigEnabled => IsModeToggleEnabled && !IsCustomConfigActive;

        /// <summary>Pushed in at startup and whenever the profile editor saves.</summary>
        public void ApplyConfigProfileState(bool useTunProfile, bool useProxyProfile)
        {
            _useTunConfigProfile   = useTunProfile;
            _useProxyConfigProfile = useProxyProfile;
            NotifyConfigProfileStateChanged();
        }

        private void NotifyConfigProfileStateChanged()
        {
            OnPropertyChanged(nameof(IsBuiltInConfigEnabled));
            OnPropertyChanged(nameof(RoutingModeText));
        }

        partial void OnIsTunModeChanged(bool value)
        {
            OnPropertyChanged(nameof(TunModeText));
            // The active slot follows the mode, so the custom-config gates move with it.
            NotifyConfigProfileStateChanged();
            OnPropertyChanged(nameof(IsModeToggleEnabled));
            if (!_isTunInternalUpdate)
                _ = HandleTunToggleAsync(value);
        }

        /// <summary>
        /// Handles user changes to the TUN toggle: when not elevated, restores the toggle and shows
        /// a confirmation dialog, then restarts the app as administrator after confirmation.
        /// </summary>
        private async Task HandleTunToggleAsync(bool wantEnable)
        {
            // No extra work is needed when disabling TUN or already elevated.
            if (!wantEnable || AdminHelper.IsAdministrator())
                return;

            // Revert the toggle before prompting for elevation.
            _isTunInternalUpdate = true;
            IsTunMode = false;
            _isTunInternalUpdate = false;

            var appSettings = await _settings.LoadSettingsAsync();
            if (!await _dialogs.ShowTunConfirmationDialogAsync(appSettings)) return;

            await TrySaveSettingsAsync(appSettings, "TUN mode settings save");

            RestartAsAdmin("--tun");
        }

        private static void RestartAsAdmin(string arguments)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;
                var currentPid = Environment.ProcessId;
                var restartArguments = string.IsNullOrWhiteSpace(arguments)
                    ? $"--parent-pid={currentPid}"
                    : $"{arguments} --parent-pid={currentPid}";

                Process.Start(new ProcessStartInfo
                {
                    FileName       = exePath,
                    Arguments      = restartArguments,
                    UseShellExecute = true,
                    Verb           = "runas"
                });

                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    try
                    {
                        Process.GetCurrentProcess().Kill();
                    }
                    catch
                    {
                        // ignored
                    }
                });

                if (Application.Current is App app)
                {
                    app.RequestShutdown();
                }
                else
                {
                    Environment.Exit(0);
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The user clicked "No" in the UAC dialog.
                Debug.WriteLine("[TUN] 用户取消了管理员授权");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 以管理员身份重启失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the TUN toggle silently without permission checks or dialogs.
        /// Called by App.xaml.cs after it detects the --tun argument.
        /// </summary>
        public void SetTunEnabledSilently(bool value)
        {
            _isTunInternalUpdate = true;
            IsTunMode = value;
            _isTunInternalUpdate = false;
        }

        // ── Local port ────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LocalPortText))]
        public partial int LocalPort { get; set; }

        [ObservableProperty]
        public partial bool AllowLanConnections { get; set; }

        public string LocalPortText => $":{LocalPort}";

        [RelayCommand]
        private async Task EditLocalPort()
        {
            var result = await _dialogs.ShowEditPortDialogAsync(LocalPort, AllowLanConnections);
            if (result.HasValue && (result.Value.port != LocalPort || result.Value.allowLan != AllowLanConnections))
            {
                LocalPort = result.Value.port;
                AllowLanConnections = result.Value.allowLan;
                var settings = await _settings.LoadSettingsAsync();
                settings.LocalMixedPort = LocalPort;
                settings.AllowLanConnections = AllowLanConnections;
                await TrySaveSettingsAsync(settings, "persist local port");

                // Apply live if xray is currently running (no-op in TUN mode, same as
                // routing/DNS changes — takes effect on the next connect there).
                if (IsRunning)
                {
                    try { await ReapplyRoutingAsync(); }
                    catch (Exception ex) { Debug.WriteLine($"[ControlPanel] Reapply after port/LAN change failed: {ex.Message}"); }
                }
            }
        }

        // ── Logs ──────────────────────────────────────────────────────────────

        [RelayCommand]
        private void ShowLogs() => ShowLogsRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void ShowPersonalize() => ShowPersonalizeRequested?.Invoke(this, EventArgs.Empty);

        [RelayCommand]
        private void ShowCustomRules()
        {
            var vm = new CustomRulesViewModel(
                _settings,
                _xray,
                _dialogs,
                ReapplyRoutingAsync);
            ShowCustomRulesRequested?.Invoke(this, vm);
        }

        [RelayCommand]
        private void ShowConfigProfiles()
        {
            // The preview has to agree with what start would build, and start reads the toolbar
            // state — so it goes through the same method start uses.
            var vm = new ConfigProfileViewModel(
                _settings, _profiles, _dialogs, () => BuildSelectedConfigPreviewAsync());

            vm.ProfileStateChanged += (_, state) =>
                ApplyConfigProfileState(state.UseTunProfile, state.UseProxyProfile);

            ShowConfigProfilesRequested?.Invoke(this, new ConfigProfileRequest(vm, IsTunMode));
        }

        [RelayCommand]
        private async Task ShowDnsSettings()
        {
            var s = await _settings.LoadSettingsAsync();
            var saved = await _dialogs.ShowDnsSettingsDialogAsync(s, IsTunMode);
            if (!saved) return;

            await TrySaveSettingsAsync(s, "persist DNS settings");

            if (IsRunning && IsTunMode) return;

            if (IsRunning)
            {
                try { await ReapplyRoutingAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[ControlPanel] Reapply after DNS change failed: {ex.Message}"); }
            }
        }

        // ── Routing mode ──────────────────────────────────────────────────────

        /// <summary>Business code: "smart" | "global". This is what gets persisted to
        /// settings.json and what XAML RadioButton.CommandParameter values match against.
        /// For display, bind to <see cref="RoutingModeText"/>.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RoutingModeText))]
        public partial string RoutingMode { get; set; }

        /// <summary>Localized display string for the status bar / mini view. A config profile
        /// owns routing outright, so neither built-in mode describes what is running.</summary>
        public string RoutingModeText =>
            IsCustomConfigActive     ? L.ControlPanel_RoutingCustom :
            RoutingMode == "global"  ? L.ControlPanel_RoutingGlobal :
                                       L.ControlPanel_RoutingSmart;

        [RelayCommand]
        private async Task SetRoutingMode(string mode)
        {
            // No-op guard: clicking the already-selected radio must not
            // trigger a wasteful xray restart.
            if (mode == RoutingMode) return;

            RoutingMode = mode;
            var s = await _settings.LoadSettingsAsync();
            s.RoutingMode = mode;
            await TrySaveSettingsAsync(s, "persist routing mode");

            // Apply live if xray is currently running (UI only allows this when !IsTunMode).
            if (IsRunning)
            {
                try { await ReapplyRoutingAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[ControlPanel] Reapply routing failed: {ex.Message}"); }
            }
        }

        // ── Proxy mode ────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGlobalProxyChecked))]
        [NotifyPropertyChangedFor(nameof(IsNoTakeoverChecked))]
        public partial bool IsSystemProxyEnabled { get; set; }

        public bool IsGlobalProxyChecked => IsSystemProxyEnabled;
        public bool IsNoTakeoverChecked  => !IsSystemProxyEnabled;

        [RelayCommand]
        private async Task SetProxyMode(string mode)
        {
            // Business code: "system" = take over WinINet system proxy, "manual" = leave
            // registry alone (user wires their apps to the local SOCKS port themselves).
            var want = mode == "system";

            // No-op guard: clicking the already-selected radio must not re-hit
            // the registry or re-write settings.
            if (want == IsSystemProxyEnabled) return;

            IsSystemProxyEnabled = want;
            var s = await _settings.LoadSettingsAsync();
            s.IsSystemProxyEnabled = IsSystemProxyEnabled;
            await TrySaveSettingsAsync(s, "persist proxy mode");

            // Apply live if xray is running outside TUN (UI prevents this call in TUN+Running).
            // Note: system proxy lives in Windows registry, not in xray config — so no
            // ReapplyRoutingAsync needed; just flip the registry flag.
            if (IsRunning && !IsTunMode)
            {
                if (IsSystemProxyEnabled)
                    ApplySystemProxy(_activeLocalProxyPort);
                else
                    SystemProxyService.ClearProxy();
            }
        }


        // ── Theme ─────────────────────────────────────────────────────────────

        public void InitializePersonalize(AppSettings settings)
        {
            ProtocolColorStore.LoadFrom(settings);
            GlobalHotkeyStore.LoadFrom(settings);

            var theme = settings.ThemeSetting switch
            {
                "Light"  => ElementTheme.Light,
                "Dark"   => ElementTheme.Dark,
                _        => ElementTheme.Default
            };

            ThemeHelper.ApplyTheme(theme);
            ThemeHelper.ApplyBackdrop(settings.BackdropSetting ?? "Mica");
        }

        private async Task TrySaveSettingsAsync(AppSettings settings, string scenario)
        {
            try
            {
                // ConfigureAwait(false) is load-bearing: CleanupTunOnExit blocks the UI
                // thread in GetResult() on this method (exit/crash paths), so resuming
                // the continuation on the dispatcher would deadlock the process.
                if (!await _settings.SaveSettingsAsync(settings).ConfigureAwait(false))
                    Debug.WriteLine($"[Settings] Refused to {scenario}: settings.json did not parse.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Failed to {scenario}: {ex.Message}");
            }
        }

        // ── App update notification ───────────────────────────────────────────────

        /// <summary>True iff a newer release was found at startup. Drives the gear
        /// button's yellow dot and the update menu item.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UpdateBadgeVisibility))]
        [NotifyPropertyChangedFor(nameof(UpdateMenuText))]
        public partial bool IsUpdateAvailable { get; private set; }

        public Visibility UpdateBadgeVisibility => IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        public string     UpdateMenuText        => Loc.Format("ControlPanel_UpdateFound", _availableUpdate?.NewVersion);

        /// <summary>Called from MainViewModel after the background check completes.
        /// Pass a null <paramref name="info"/> to clear (e.g. after a failed update
        /// attempt). <paramref name="notes"/> is the already-fetched release notes
        /// shown on the confirm dialog; empty means none.</summary>
        public void SetAvailableUpdate(UpdateInfo? info, IReadOnlyList<string> notes)
        {
            _availableUpdate = info;
            _availableUpdateNotes = notes;
            IsUpdateAvailable = info is not null;
        }

        [RelayCommand]
        private async Task UpdateAppAsync()
        {
            var info = _availableUpdate;
            if (info is null) return;

            if (!await _dialogs.ShowUpdateConfirmDialogAsync(info.NewVersion, _availableUpdateNotes))
                return;

            // Route the download through xray when it's running so users behind GFW
            // can still reach github.com / objects.githubusercontent.com.
            var proxy = ActiveLocalProxyPort is { } port ? $"socks5://127.0.0.1:{port}" : null;

            UpdateStaging? staging = null;
            try
            {
                await _dialogs.ShowProgressBarDialogAsync(L.Update_Updating,
                    async (progress, ct) =>
                    {
                        staging = await _update.DownloadVerifyAndExtractAsync(info, proxy, progress, ct);
                    });
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
            {
                // User cancel — silent.
                return;
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync(L.Error_UpdateFailed, ex.Message);
                return;
            }

            if (staging is null) return;

            // Start the updater first; it waits for our PID to exit. The normal
            // interactive stop path can block on route/process cleanup, so update
            // handoff uses the bounded shutdown cleanup instead.
            try
            {
                _update.LaunchUpdater(staging);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync(L.Error_UpdateFailed, Loc.Format("Error_UpdaterLaunchFailed", ex.Message));
                return;
            }

            if (Application.Current is App app)
                app.RequestShutdown(fastShutdown: true);
            else
            {
                SystemProxyService.ClearProxy();
                _xray.StopForShutdown();
                CleanupTunOnExit(fastShutdown: true);
                Environment.Exit(0);
            }
        }
    }
}
