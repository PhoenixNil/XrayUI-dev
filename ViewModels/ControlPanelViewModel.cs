using System;
using System.Diagnostics;
using System.Net.Http;
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
        private readonly StartupService _startupService;
        private readonly GeoDataUpdateService _geoUpdate = new();
        private string _startStopButtonContent = "启动";
        private bool _startStopButtonChecked;
        private bool _isRunning;
        private bool _isTunMode;
        private int _localPort = 16890;
        private string _routingMode = "智能分流";
        private bool _isSystemProxyEnabled = true;
        private bool _isStartupEnabled;
        private bool _isAutoConnect;
        // Guards OnIsTunModeChanged from firing the dialog when we update internally
        private bool _isTunInternalUpdate;

        // Tracks the server host of the currently active TUN session (for cleanup)
        private string? _currentTunServerHost;

        public XrayService XrayService => _xray;

        public Func<ServerEntry?> GetSelectedServer { get; set; } = () => null;

        // Snapshot of the server xray is actually running with, so reapply restarts
        // against the live session rather than whatever is now selected in the list.
        private ServerEntry? _activeServer;
        private string _activeServerName = string.Empty;

        // Serializes concurrent reapply calls (custom-rules save, routing-mode toggle,
        // proxy-mode toggle can all race) and blocks re-entry.
        private readonly SemaphoreSlim _reapplyLock = new(1, 1);
        private bool _isReapplying;

        /// <summary>True while ReapplyRoutingAsync is mid-restart. UI uses this to
        /// disable related menu items and show "正在应用...".</summary>
        public bool IsReapplying
        {
            get => _isReapplying;
            private set
            {
                if (SetProperty(ref _isReapplying, value))
                {
                    OnPropertyChanged(nameof(IsModeToggleEnabled));
                    OnPropertyChanged(nameof(IsTunToggleEnabled));
                    OnPropertyChanged(nameof(IsNotReapplying));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>Inverse of <see cref="IsReapplying"/> for x:Bind IsEnabled targets
        /// (x:Bind doesn't support expression negation).</summary>
        public bool IsNotReapplying => !_isReapplying;

        public event EventHandler? ShowLogsRequested;
        public event EventHandler? ShowPersonalizeRequested;
        public event EventHandler<CustomRulesViewModel>? ShowCustomRulesRequested;

        public ControlPanelViewModel(
            IDialogService dialogs,
            SettingsService settings,
            XrayService xray,
            TunService tunService,
            StartupService startupService)
        {
            _dialogs        = dialogs;
            _settings       = settings;
            _xray           = xray;
            _tunService     = tunService;
            _startupService = startupService;
        }

        // ── Running state ─────────────────────────────────────────────────────────────────────────────────────────────

        public string StartStopButtonContent
        {
            get => _startStopButtonContent;
            private set => SetProperty(ref _startStopButtonContent, value);
        }

        public bool StartStopButtonChecked
        {
            get => _startStopButtonChecked;
            private set => SetProperty(ref _startStopButtonChecked, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnIsRunningChanged(value);
                }
            }
        }

        public string StatusText =>
            IsReapplying ? "正在应用..." :
            IsRunning    ? _activeServerName :
                           "未运行";

        private void OnIsRunningChanged(bool value)
        {
            StartStopButtonContent = value ? "停止" : "启动";
            StartStopButtonChecked = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsModeToggleEnabled));
            OnPropertyChanged(nameof(IsTunToggleEnabled));
        }

        // ── Start / Stop ──────────────────────────────────────────────────────

        [RelayCommand]
        private async Task StartStop()
        {
            if (IsReapplying) return;

            try
            {
                if (IsRunning)
                {
                    await StopCurrentSessionAsync();
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
            if (_isSystemProxyEnabled && !IsTunMode)
                SystemProxyService.ClearProxy();
            _activeServer     = null;
            _activeServerName = string.Empty;
            IsRunning = false;
        }

        private async Task<bool> StartSelectedServerAsync()
        {
            var server = GetSelectedServer();
            if (server is null)
            {
                await _dialogs.ShowErrorAsync("未选择服务器", "请先从列表中选择服务器");
                return false;
            }

            var appSettings = await _settings.LoadSettingsAsync();
            appSettings.LocalMixedPort = LocalPort;
            appSettings.RoutingMode    = RoutingMode == "智能分流" ? "smart" : "global";
            appSettings.IsTunMode      = IsTunMode;
            if (IsAutoConnect)
                appSettings.LastAutoConnectServerId = server.Id;

            string? tunOutboundInterfaceName = null;
            if (IsTunMode)
            {
                // Pre-check: wintun.dll must be present so xray can create the TUN adapter.
                if (!_tunService.IsWintunAvailable())
                {
                    await _dialogs.ShowErrorAsync("TUN mode error",
                        $"Could not find wintun.dll\nPath: {_tunService.GetExpectedWintunPath()}");
                    return false;
                }

                tunOutboundInterfaceName = _tunService.DetectDefaultOutboundInterfaceName();
                if (string.IsNullOrWhiteSpace(tunOutboundInterfaceName))
                {
                    await _dialogs.ShowErrorAsync("TUN mode error",
                        "Could not determine the default outbound network interface. Please check that Wi-Fi/Ethernet is connected, then try TUN mode again as administrator.");
                    return false;
                }

                SystemProxyService.ClearProxy();
                await CleanupPersistedTunRoutesAsync(appSettings);
            }

            var configJson = XrayConfigBuilder.Build(server, appSettings, tunOutboundInterfaceName);
            var ok = await _xray.StartAsync(configJson);

            if (!ok)
            {
                var detail = string.IsNullOrEmpty(_xray.LastError)
                    ? "xray 启动失败. 请检查服务器配置."
                    : _xray.LastError;
                await _dialogs.ShowErrorAsync("启动失败", detail);
                return false;
            }

            if (IsTunMode)
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
                appSettings.IsSystemProxyEnabled = _isSystemProxyEnabled;
                if (_isSystemProxyEnabled)
                    SystemProxyService.SetProxy("127.0.0.1", appSettings.LocalMixedPort);
                await TrySaveSettingsAsync(appSettings, "persist system proxy settings");
            }

            _activeServer     = server;
            _activeServerName = server.Name;
            IsRunning = true;

            // Warm up connectivity in the background after TUN startup.
            if (IsTunMode)
                _ = WarmUpTunInBackgroundAsync();

            return true;
        }

        private async Task HandleStartStopFailureAsync(Exception ex)
        {
            Debug.WriteLine($"[ControlPanel] Start/stop failed: {ex}");

            if (_xray.IsRunning)
            {
                await _xray.StopAsync();
            }

            SystemProxyService.ClearProxy();
            _activeServer     = null;
            _activeServerName = string.Empty;
            IsRunning = false;
            await _dialogs.ShowErrorAsync("启动失败", ex.Message);
        }

        /// <summary>
        /// Rebuild xray config from persisted settings and restart xray. No-op if not running.
        /// Always reapplies against the live _activeServer, not the currently-selected list entry.
        /// Not safe in TUN mode: restarting xray tears down the TUN adapter but we don't
        /// re-invoke SetupTunRoutes, so traffic would silently fall off the tunnel.
        /// </summary>
        public async Task ReapplyRoutingAsync()
        {
            if (!IsRunning) return;
            if (_activeServer is null) return;
            if (IsTunMode) return;

            await _reapplyLock.WaitAsync();
            try
            {
                if (!IsRunning || _activeServer is null) return;

                IsReapplying = true;
                try
                {
                    var settings = await _settings.LoadSettingsAsync();
                    var cfg = XrayConfigBuilder.Build(_activeServer, settings);
                    var ok = await _xray.StartAsync(cfg);
                    if (!ok)
                    {
                        var detail = string.IsNullOrEmpty(_xray.LastError)
                            ? "xray 应用新配置失败，已停止。"
                            : _xray.LastError;
                        await HandleReapplyFailureAsync(detail);
                    }
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

            _activeServer     = null;
            _activeServerName = string.Empty;
            IsRunning = false;

            await _dialogs.ShowErrorAsync("应用新配置失败", detail);
        }

        /// <summary>
        /// 后台轻量预热：发几个 HTTP 探测让 Windows 路由表和 DNS 缓存生效。
        /// 最多 3 轮 × 1.5s 超时 + 500ms 间隔 ≈ 最坏 ~7 秒，且完全不阻塞 UI。
        /// </summary>
        private async Task WarmUpTunInBackgroundAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using var response = await client.GetAsync("https://www.gstatic.com/generate_204",
                            HttpCompletionOption.ResponseHeadersRead);
                        if ((int)response.StatusCode < 500)
                            return;
                    }
                    catch
                    {
                        // Ignore and retry.
                    }

                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 后台预热异常: {ex.Message}");
            }
        }

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

        /// <summary>供 MainWindow.StopBackgroundServicesOnExit 调用，确保退出时清理路由</summary>
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

            CleanupTunRoutesSafely();
            settings.LastTunServerHost = null;
            await TrySaveSettingsAsync(settings, "clear persisted TUN routes");
        }

        private async Task CleanupTunStateAsync()
        {
            CleanupTunRoutesSafely();

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

        public bool IsTunMode
        {
            get => _isTunMode;
            set
            {
                if (SetProperty(ref _isTunMode, value))
                {
                    OnIsTunModeChanged(value);
                }
            }
        }

        public string TunModeText => IsTunMode ? "On" : "Off";

        /// <summary>
        /// 路由模式 / 代理模式的可切换性。
        /// 运行时切换会自动 reapply；但 TUN 模式运行中禁止改，避免把 TUN 管道搞混。
        /// Reapply 进行时也禁用，防止重入。
        /// </summary>
        public bool IsModeToggleEnabled => !IsReapplying && !(IsRunning && IsTunMode);

        /// <summary>TUN 开关自身：运行中禁止切换（切 TUN 要重启 xray + 改网络栈）。
        /// Reapply 进行时也禁用。</summary>
        public bool IsTunToggleEnabled => !IsRunning && !IsReapplying;

        private void OnIsTunModeChanged(bool value)
        {
            OnPropertyChanged(nameof(TunModeText));
            OnPropertyChanged(nameof(IsModeToggleEnabled));
            if (!_isTunInternalUpdate)
                _ = HandleTunToggleAsync(value);
        }

        /// <summary>
        /// 处理用户切换 TUN 开关：非管理员时还原开关并弹出确认对话框，
        /// 用户确认后以管理员身份重启 App。
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

            var confirmed = await _dialogs.ShowConfirmationAsync(
                "开启TUN模式",
                "开启 TUN 模式需要管理员权限，程序将会重启，是否继续？",
                "确认",
                "取消");

            if (!confirmed) return;

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
                // 用户点击了 UAC 对话框的"否"
                Debug.WriteLine("[TUN] 用户取消了管理员授权");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TUN] 以管理员身份重启失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 静默设置 TUN 开关（不触发权限检查和对话框）。
        /// 供 App.xaml.cs 在检测到 --tun 参数后调用。
        /// </summary>
        public void SetTunEnabledSilently(bool value)
        {
            _isTunInternalUpdate = true;
            IsTunMode = value;
            _isTunInternalUpdate = false;
        }

        // ── Local port ────────────────────────────────────────────────────────

        public int LocalPort
        {
            get => _localPort;
            set
            {
                if (SetProperty(ref _localPort, value))
                {
                    OnPropertyChanged(nameof(LocalPortText));
                }
            }
        }

        public string LocalPortText => $":{LocalPort}";

        [RelayCommand]
        private async Task EditLocalPort()
        {
            var newPort = await _dialogs.ShowEditPortDialogAsync(LocalPort);
            if (newPort.HasValue)
            {
                LocalPort = newPort.Value;
                var settings = await _settings.LoadSettingsAsync();
                settings.LocalMixedPort = LocalPort;
                await TrySaveSettingsAsync(settings, "persist local port");
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
                _geoUpdate,
                _dialogs,
                ReapplyRoutingAsync,
                () => IsTunMode,
                // In TUN mode the local SOCKS port still proxies traffic for non-TUN-captured
                // processes (including ourselves), so routing the download through it is fine.
                // When xray is stopped, null = direct connection.
                () => _xray.IsRunning ? $"socks5://127.0.0.1:{LocalPort}" : null);
            ShowCustomRulesRequested?.Invoke(this, vm);
        }

        // ── Routing mode ──────────────────────────────────────────────────────

        public string RoutingMode
        {
            get => _routingMode;
            set => SetProperty(ref _routingMode, value);
        }

        [RelayCommand]
        private async Task SetRoutingMode(string mode)
        {
            // No-op guard: clicking the already-selected radio must not
            // trigger a wasteful xray restart.
            if (mode == _routingMode) return;

            RoutingMode = mode;
            var s = await _settings.LoadSettingsAsync();
            s.RoutingMode = mode == "智能分流" ? "smart" : "global";
            await TrySaveSettingsAsync(s, "persist routing mode");

            // Apply live if xray is currently running (UI only allows this when !IsTunMode).
            if (IsRunning)
            {
                try { await ReapplyRoutingAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[ControlPanel] Reapply routing failed: {ex.Message}"); }
            }
        }

        // ── Proxy mode ────────────────────────────────────────────────────────

        public bool IsSystemProxyEnabled
        {
            get => _isSystemProxyEnabled;
            set
            {
                if (SetProperty(ref _isSystemProxyEnabled, value))
                {
                    OnPropertyChanged(nameof(IsGlobalProxyChecked));
                    OnPropertyChanged(nameof(IsNoTakeoverChecked));
                }
            }
        }

        public bool IsGlobalProxyChecked => _isSystemProxyEnabled;
        public bool IsNoTakeoverChecked  => !_isSystemProxyEnabled;

        [RelayCommand]
        private async Task SetProxyMode(string mode)
        {
            var want = mode == "全局代理";

            // No-op guard: clicking the already-selected radio must not re-hit
            // the registry or re-write settings.
            if (want == _isSystemProxyEnabled) return;

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
                    SystemProxyService.SetProxy("127.0.0.1", s.LocalMixedPort);
                else
                    SystemProxyService.ClearProxy();
            }
        }

        // ── Startup ───────────────────────────────────────────────────────────

        public bool IsStartupEnabled
        {
            get => _isStartupEnabled;
            set
            {
                if (SetProperty(ref _isStartupEnabled, value))
                    OnPropertyChanged(nameof(StartupMenuIcon));
            }
        }

        public bool IsAutoConnect
        {
            get => _isAutoConnect;
            set => SetProperty(ref _isAutoConnect, value);
        }

        /// <summary>
        /// Returns a checkmark icon when auto-start is enabled, null otherwise.
        /// Bound to MenuFlyoutItem.Icon so the item reflects current state without
        /// using ToggleMenuFlyoutItem (which has timing issues with Command).
        /// </summary>
        private static readonly FontIcon _startupIcon = new() { Glyph = "\uE73E" };
        public IconElement? StartupMenuIcon => _isStartupEnabled ? _startupIcon : null;

        [RelayCommand]
        private async Task OpenStartupSettings()
        {
            // When startup is off, always show auto-connect as unchecked to avoid confusion.
            var result = await _dialogs.ShowStartupDialogAsync(IsStartupEnabled, IsStartupEnabled && IsAutoConnect);
            if (result is null) return;   // user cancelled — leave state unchanged

            var (newEnabled, newAutoConnect) = result.Value;

            var s = await _settings.LoadSettingsAsync();
            try
            {
                _startupService.SetStartupEnabled(newEnabled);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync("开机启动设置失败", ex.Message);
                return;
            }

            s.IsStartupEnabled = newEnabled;
            s.IsAutoConnect    = newAutoConnect;
            if (!newAutoConnect)
                s.LastAutoConnectServerId = null;
            else if (IsRunning && _activeServer is not null)
                s.LastAutoConnectServerId = _activeServer.Id;
            await TrySaveSettingsAsync(s, "persist startup settings");

            IsStartupEnabled = newEnabled;
            IsAutoConnect    = newAutoConnect;
        }

        // ── Theme ─────────────────────────────────────────────────────────────

        public void InitializePersonalize(AppSettings settings)
        {
            ProtocolColorStore.LoadFrom(settings);

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
                await _settings.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Failed to {scenario}: {ex.Message}");
            }
        }
    }
}
