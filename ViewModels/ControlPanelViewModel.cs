using System;
using System.Diagnostics;
using System.Net.Http;
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
        private string _startStopButtonContent = "Start";
        private bool _startStopButtonChecked;
        private bool _isRunning;
        private bool _isTunMode;
        private int _localPort = 16890;
        private string _routingMode = "智能分流";
        private bool _isThemePickerOpen;

        // Guards OnIsTunModeChanged from firing the dialog when we update internally
        private bool _isTunInternalUpdate;

        // Tracks the server host of the currently active TUN session (for cleanup)
        private string? _currentTunServerHost;

        public XrayService XrayService => _xray;

        public Func<ServerEntry?> GetSelectedServer { get; set; } = () => null;

        private string _activeServerName = string.Empty;

        public event EventHandler? ShowLogsRequested;

        public ControlPanelViewModel(
            IDialogService dialogs,
            SettingsService settings,
            XrayService xray,
            TunService tunService)
        {
            _dialogs    = dialogs;
            _settings   = settings;
            _xray       = xray;
            _tunService = tunService;
        }

        // ── Running state ─────────────────────────────────────────────────────────────────────────────────────────────

        public string StartStopButtonContent
        {
            get => _startStopButtonContent;
            set => SetProperty(ref _startStopButtonContent, value);
        }

        public bool StartStopButtonChecked
        {
            get => _startStopButtonChecked;
            set => SetProperty(ref _startStopButtonChecked, value);
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

        public string StatusText => IsRunning ? _activeServerName : "未运行";

        private void OnIsRunningChanged(bool value)
        {
            StartStopButtonContent = value ? "停止" : "启动";
            StartStopButtonChecked = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsTunModeToggleEnabled));
        }

        // ── Start / Stop ──────────────────────────────────────────────────────

        [RelayCommand]
        private async Task StartStop()
        {
            try
            {
                if (IsRunning)
                {
                    // ── STOP ──
                    await CleanupTunStateAsync();
                    await _xray.StopAsync();
                    SystemProxyService.ClearProxy();
                    _activeServerName = string.Empty;
                    IsRunning = false;
                    return;
                }

                // ── START ──
                var server = GetSelectedServer();
                if (server is null)
                {
                    await _dialogs.ShowErrorAsync("No server selected", "Please select a server from the list first.");
                    return;
                }

                var appSettings = await _settings.LoadSettingsAsync();
                appSettings.LocalSocksPort = LocalPort;
                appSettings.LocalHttpPort  = LocalPort + 1;
                appSettings.RoutingMode    = RoutingMode == "智能分流" ? "smart" : "global";
                appSettings.IsTunMode      = IsTunMode;

                string? outboundInterface = null;

                if (IsTunMode)
                {
                    // Pre-check 1: wintun.dll
                    if (!_tunService.IsWintunAvailable())
                    {
                        await _dialogs.ShowErrorAsync("TUN mode error",
                            $"Could not find wintun.dll\nPath: {_tunService.GetExpectedWintunPath()}");
                        return;
                    }

                    // Pre-check 2: detect outbound NIC
                    outboundInterface = _tunService.DetectOutboundInterface(forceRefresh: true);
                    if (string.IsNullOrEmpty(outboundInterface))
                    {
                        await _dialogs.ShowErrorAsync("TUN mode error", "Unable to detect a valid outbound network interface. Please check your network connection.");
                        return;
                    }
                }

                if (IsTunMode)
                {
                    SystemProxyService.ClearProxy();
                    await CleanupPersistedTunRoutesAsync(appSettings);
                }

                var configJson = XrayConfigBuilder.Build(server, appSettings, outboundInterface);
                var ok = await _xray.StartAsync(configJson);

                if (!ok)
                {
                    var detail = string.IsNullOrEmpty(_xray.LastError)
                        ? "xray failed to start. Please check the server configuration."
                        : _xray.LastError;
                    await _dialogs.ShowErrorAsync("启动失败", detail);
                    return;
                }

                if (IsTunMode)
                {
                    // Wait for xray to create the TUN adapter, then add system routes
                    _currentTunServerHost = server.Host;
                    try
                    {
                        await WaitForTunInterfaceAsync();
                        if (!_tunService.SetupTunRoutes(server.Host))
                        {
                            throw new InvalidOperationException("Failed to set up TUN routes. Please confirm the app is running as administrator.");
                        }
                    }
                    catch (Exception ex)
                    {
                        await CleanupTunStateAsync();
                        await _xray.StopAsync();
                        await _dialogs.ShowErrorAsync("TUN 启动失败", ex.Message);
                        return;
                    }
                    // TUN 模式是透明代理，不设置系统 HTTP 代理
                    appSettings.LastTunServerHost = server.Host;
                    await TrySaveSettingsAsync(appSettings, "persist TUN runtime state");
                }
                else
                {
                    appSettings.LastTunServerHost = null;
                    SystemProxyService.SetProxy("127.0.0.1", appSettings.LocalHttpPort);
                    await TrySaveSettingsAsync(appSettings, "persist system proxy settings");
                }

                _activeServerName = server.Name;
                IsRunning = true;

                // Warm up connectivity in the background after TUN startup.
                if (IsTunMode)
                    _ = WarmUpTunInBackgroundAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ControlPanel] StartStop failed: {ex}");

                if (_xray.IsRunning)
                {
                    await _xray.StopAsync();
                }

                SystemProxyService.ClearProxy();
                _activeServerName = string.Empty;
                IsRunning = false;
                await _dialogs.ShowErrorAsync("启动失败", ex.Message);
            }
        }

        /// <summary>轮询等待 xray 创建 TUN 网络适配器（最多 15 秒）</summary>
        private async Task WaitForTunInterfaceAsync()
        {
            const int pollMs = 500;
            const int maxMs  = 15000;
            int elapsed = 0;

            while (elapsed < maxMs)
            {
                await Task.Delay(pollMs);
                elapsed += pollMs;

                if (!_xray.IsRunning)
                    throw new InvalidOperationException("xray exited unexpectedly before the TUN interface was created. Please check the logs.");

                if (_tunService.GetTunInterfaceIndex() != null)
                    return;
            }

            throw new InvalidOperationException("Timed out waiting for the TUN interface. Please try again.");
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

        public void CleanupTunOnExit()
        {
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

        public bool IsTunModeToggleEnabled => !IsRunning;

        private void OnIsTunModeChanged(bool value)
        {
            OnPropertyChanged(nameof(TunModeText));
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
                "继续",
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
                    }
                });

                if (global::Microsoft.UI.Xaml.Application.Current is App app)
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
                settings.LocalSocksPort = LocalPort;
                settings.LocalHttpPort  = LocalPort + 1;
                await TrySaveSettingsAsync(settings, "persist local port");
            }
        }

        // ── Logs ──────────────────────────────────────────────────────────────

        [RelayCommand]
        private void ShowLogs() => ShowLogsRequested?.Invoke(this, EventArgs.Empty);

        // ── Routing mode ──────────────────────────────────────────────────────

        public string RoutingMode
        {
            get => _routingMode;
            set => SetProperty(ref _routingMode, value);
        }

        [RelayCommand]
        private void SetRoutingMode(string mode) => RoutingMode = mode;

        // ── Theme ─────────────────────────────────────────────────────────────

        public ElementTheme LightTheme   => ElementTheme.Light;
        public ElementTheme DarkTheme    => ElementTheme.Dark;
        public ElementTheme DefaultTheme => ElementTheme.Default;

        public bool IsLightThemeEnabled   => ThemeHelper.ActualTheme != ElementTheme.Light;
        public bool IsDarkThemeEnabled    => ThemeHelper.ActualTheme != ElementTheme.Dark;
        public bool IsDefaultThemeEnabled => true;

        public bool IsThemePickerOpen
        {
            get => _isThemePickerOpen;
            set => SetProperty(ref _isThemePickerOpen, value);
        }

        [RelayCommand]
        private void ToggleThemePicker() => IsThemePickerOpen = !IsThemePickerOpen;

        [RelayCommand]
        private void ChangeTheme(ElementTheme? theme)
        {
            if (!theme.HasValue) return;

            ThemeHelper.ApplyTheme(theme.Value);
            OnPropertyChanged(nameof(IsLightThemeEnabled));
            OnPropertyChanged(nameof(IsDarkThemeEnabled));
            OnPropertyChanged(nameof(IsDefaultThemeEnabled));
            IsThemePickerOpen = false;
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


