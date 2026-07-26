using System.ComponentModel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class MainViewModel : BaseViewModel
    {
        private readonly SettingsService _settings;
        private readonly StartupService _startupService;
        private readonly IUpdateService _updateService;
        private readonly DispatcherQueue? _uiDispatcher;
        private DispatcherQueueTimer? _subscriptionRefreshTimer;
        private bool _updateCheckQueued;
        private ServerEntry? _activeServer;
        private string _activeLatencyText = string.Empty;
        private bool _showPersonalize;

        public ServerListViewModel   ServerList   { get; }
        public ServerDetailViewModel ServerDetail { get; }
        public ControlPanelViewModel ControlPanel { get; }
        public PersonalizeViewModel  Personalize  { get; }

        public Visibility MainContentVisibility => _showPersonalize ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PersonalizeVisibility  => _showPersonalize ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility BackButtonVisibility   => _showPersonalize ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility MiniModeVisibility     => IsMiniMode       ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility FullModeVisibility     => IsMiniMode       ? Visibility.Collapsed : Visibility.Visible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MiniModeVisibility))]
        [NotifyPropertyChangedFor(nameof(FullModeVisibility))]
        public partial bool IsMiniMode { get; set; }

        public string ActiveServerName =>
            (ControlPanel.IsRunning ? _activeServer : ServerList.SelectedServer)?.Name ?? L.Main_NoSelection;

        // Tray icon tooltip. Uses (IsRunning || IsReapplying) so it stays in the "running"
        // form across a node switch — IsReapplying brackets the stop→start gap (the same
        // masking StatusText relies on), so the tray text never flickers to "idle" mid-switch.
        public string TrayTooltip =>
            (ControlPanel.IsRunning || ControlPanel.IsReapplying)
                ? Loc.Format("Tray_TooltipRunning", ActiveServerName)
                : L.Tray_TooltipIdle;

        // True while the tray + taskbar icon should show the "connected" (green-dot) variant.
        // Mirrors TrayTooltip's condition exactly so icon and tooltip flip together — including
        // staying "running" across a node switch (IsReapplying brackets the stop→start gap).
        // Read by MainWindow inside the TrayTooltip PropertyChanged handler, so it does not need
        // to raise its own change notification.
        public bool TrayShowsRunning => ControlPanel.IsRunning || ControlPanel.IsReapplying;

        public string MiniRoutingMode => ControlPanel.RoutingModeText;
        public IAsyncRelayCommand MiniStartStopCommand => ControlPanel.StartStopCommand;
        public bool MiniIsRunning => ControlPanel.IsRunning;
        public string MiniStatusText => ControlPanel.IsRunning ? _activeLatencyText : L.Main_NotConnected;
        public Visibility MiniDotVisibility => ControlPanel.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        public MainViewModel(
            IDialogService  dialogs,
            SettingsService settings,
            XrayService     xray,
            TunService      tunService,
            StartupService  startupService,
            IUpdateService  updateService)
        {
            _settings       = settings;
            _startupService = startupService;
            _updateService  = updateService;
            // MainViewModel is constructed on the UI thread (in MainWindow ctor before
            // InitializeComponent), so capturing the dispatcher here is safe and avoids
            // depending on Application.Current later from a background thread.
            _uiDispatcher   = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var latencyProbe = new LatencyProbeService(
                new TcpConnectProbeService(),
                new PingProbeService());
            var realLatencyProbe = new RealLatencyProbeService(settings, tunService);
            var aiUnlockCheck = new AiUnlockCheckService();

            Title = "Proxy Console";

            ServerList   = new ServerListViewModel(dialogs, settings, latencyProbe, realLatencyProbe);
            ServerDetail = new ServerDetailViewModel(latencyProbe, aiUnlockCheck);
            ControlPanel = new ControlPanelViewModel(dialogs, settings, xray, tunService, startupService, updateService);
            Personalize  = new PersonalizeViewModel(dialogs, settings);

            // Wire ControlPanel so it knows the current selected server
            ControlPanel.GetSelectedServer = () => ServerList.SelectedServer;
            ControlPanel.GetAllServers = () => ServerList.Servers;
            ControlPanel.CanStartSelectedServer = () => ServerList.CanRunSelectedServer;
            ServerDetail.GetAllServers = () => ServerList.Servers;
            ServerDetail.ResolveGroupName = ServerList.GetGroupDisplayName;
            ServerDetail.OpenSubscriptions = ServerList.OpenSubscriptionsOnManagePageAsync;
            // A subscription rename/delete changes the detail pane's group label without touching
            // the selected node, so property notifications on ServerEntry can't cover it.
            ServerList.GroupNamesChanged += ServerDetail.RefreshGroupName;
            ServerList.RequestSwitchToSelectedServer = ControlPanel.SwitchToSelectedServerAsync;
            Personalize.IsProxyRunning = () => ControlPanel.IsRunning;
            // Live TUN state for the speed test's egress pin — settings.IsTunMode alone lags
            // the UI toggle and can survive a crash as a stale true (see IDialogService remarks).
            realLatencyProbe.IsTunActive = () => ControlPanel.IsRunning && ControlPanel.IsTunMode;

            ServerList.PropertyChanged   += OnServerListPropertyChanged;
            ControlPanel.PropertyChanged += OnControlPanelPropertyChanged;
            ServerDetail.PropertyChanged += OnServerDetailPropertyChanged;
            Personalize.PropertyChanged  += OnPersonalizePropertyChanged;

            ControlPanel.ShowPersonalizeRequested += (_, _) => OpenPersonalize();
            Personalize.CloseRequested            += (_, _) => ClosePersonalize();
            Personalize.PresetImported            += OnPresetImported;

            ServerDetail.SelectedServer = ServerList.SelectedServer;
        }

        // ── Startup initialisation (call after Window is ready) ───────────────

        public async Task InitializeAsync(bool isBootLaunch = false)
        {
            await new InitialImportService(_settings).ImportAsync();

            // Load saved server list
            await ServerList.LoadServersAsync();

            // Sync ServerDetail with whatever was selected
            ServerDetail.SelectedServer = ServerList.SelectedServer;
            ClearActiveServerFlags();
            UpdateActiveServer(null);
            ServerList.IsProxyRunning = ControlPanel.IsRunning;

            // Load settings and apply to ControlPanel
            var s = await _settings.LoadSettingsAsync();
            ControlPanel.LocalPort             = s.LocalMixedPort;
            ControlPanel.AllowLanConnections   = s.AllowLanConnections;
            ControlPanel.RoutingMode           = s.RoutingMode;
            ControlPanel.IsSystemProxyEnabled  = s.IsSystemProxyEnabled;
            ControlPanel.InitializePersonalize(s);
            Personalize.LoadDisplayOptions(s);
            Personalize.LoadLanguage(s);
            Personalize.LoadRegion(s);
            ServerDetail.ShowLatencyInDetails = s.ShowLatencyInDetails;
            ServerDetail.ShowAiUnlockInDetails = s.ShowAiUnlockInDetails;
            ServerDetail.ShowGroupInDetails = s.ShowGroupInDetails;
            ServerList.IsFilterPanelOpen = s.OpenServerFilterPanelOnStartup;

            // Reconcile external state vs persisted setting (external is ground truth)
            var externalEnabled = await Task.Run(_startupService.IsStartupEnabled);
			if (s.IsStartupEnabled != externalEnabled)
            {
                s.IsStartupEnabled = externalEnabled;
                await _settings.SaveSettingsAsync(s);
            }
            ControlPanel.IsStartupEnabled = s.IsStartupEnabled;
            ControlPanel.IsAutoConnect    = s.IsAutoConnect;

            // Translate the legacy name-based auto-connect setting to Id-based so users
            // don't lose their auto-connect target after upgrading.
            if (string.IsNullOrEmpty(s.LastAutoConnectServerId) && !string.IsNullOrEmpty(s.LastAutoConnectServerName))
            {
                var legacy = ServerList.Servers.FirstOrDefault(
                    x => string.Equals(x.Name, s.LastAutoConnectServerName, System.StringComparison.OrdinalIgnoreCase));
                if (legacy is not null)
                    s.LastAutoConnectServerId = legacy.Id;
                s.LastAutoConnectServerName = null;
                await _settings.SaveSettingsAsync(s);
            }

            // Only auto-connect when the app was actually launched by the boot task
            // (which passes --startup-minimized). Manual launches must not auto-connect.
            if (isBootLaunch && s.IsStartupEnabled && s.IsAutoConnect)
                await TryAutoConnectAsync(s);

            await ServerList.InitializeSubscriptionRefreshSchedulesAsync(DateTimeOffset.UtcNow);
            StartSubscriptionRefreshScheduler();

            // Fire-and-forget background tasks. Failures here must never block
            // startup or surface as dialogs (per the auto-update failure policy).
            _ = Task.Run(() => _updateService.CleanupOldStagingDirs());
            QueueUpdateCheck(CurrentProxyUrl());
        }

        private void StartSubscriptionRefreshScheduler()
        {
            if (_uiDispatcher is null || _subscriptionRefreshTimer is not null) return;

            var timer = _uiDispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMinutes(1);
            timer.IsRepeating = true;
            timer.Tick += OnSubscriptionRefreshTimerTick;
            _subscriptionRefreshTimer = timer;
            timer.Start();

            // Check once at startup so a schedule missed while the app was closed is caught up.
            // Lands immediately on the boot path, where auto-connect has already run above; on a
            // manual launch the proxy is still down and the sweep declines (see
            // RefreshDueSubscriptionsAsync) until the connect below wakes it.
            _ = RunSubscriptionRefreshCheckAsync();
        }

        private async void OnSubscriptionRefreshTimerTick(DispatcherQueueTimer sender, object args)
        {
            await RunSubscriptionRefreshCheckAsync();
        }

        /// <summary>
        /// Fired from three places (startup, the minute timer, and connecting), so calls can overlap;
        /// re-entrancy is excluded by RefreshDueSubscriptionsAsync, which owns the sweep state. All
        /// this layer adds is the catch — scheduled refreshes are silent background work, per-entry
        /// failures are already handled inside the shared batch runner, and anything unexpected must
        /// stay out of startup and off the UI.
        /// </summary>
        private async Task RunSubscriptionRefreshCheckAsync()
        {
            try
            {
                await ServerList.RefreshDueSubscriptionsAsync(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Subscriptions] Scheduled refresh check failed: {ex}");
            }
        }

        public void StopSubscriptionRefreshScheduler()
        {
            var timer = _subscriptionRefreshTimer;
            _subscriptionRefreshTimer = null;
            if (timer is null) return;

            void StopTimer()
            {
                try
                {
                    timer.Stop();
                    timer.Tick -= OnSubscriptionRefreshTimerTick;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Subscriptions] Failed to stop refresh timer: {ex.Message}");
                }
            }

            if (_uiDispatcher?.HasThreadAccess != false)
                StopTimer();
            else
                _uiDispatcher.TryEnqueue(StopTimer);
        }

        private string? CurrentProxyUrl() =>
            ControlPanel.IsRunning ? $"socks5://127.0.0.1:{ControlPanel.LocalPort}" : null;

        private void QueueUpdateCheck(string? proxyUrl)
        {
            if (_updateCheckQueued) return;
            _updateCheckQueued = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await _updateService.CheckAsync(proxyUrl, CancellationToken.None);
                    if (info is null)
                    {
                        // Allow one retry path: e.g. direct check failed because the
                        // user is behind GFW; once xray comes up the recheck in
                        // OnControlPanelPropertyChanged can try again via SOCKS.
                        _updateCheckQueued = false;
                        return;
                    }
                    _uiDispatcher?.TryEnqueue(() => ControlPanel.SetAvailableUpdate(info));
                }
                catch
                {
                    _updateCheckQueued = false;
                }
            });
        }

        private async Task TryAutoConnectAsync(AppSettings s)
        {
            var target = (!string.IsNullOrEmpty(s.LastAutoConnectServerId)
                ? ServerList.Servers.FirstOrDefault(
                    x => string.Equals(x.Id, s.LastAutoConnectServerId, System.StringComparison.Ordinal))
                : null)
                ?? ServerList.Servers.FirstOrDefault();

            if (target is null) return;
            ServerList.SelectedServer = target;
            if (!ControlPanel.StartStopCommand.CanExecute(null)) return;
            await ControlPanel.StartStopCommand.ExecuteAsync(null);
        }

        // ── Personalize navigation ────────────────────────────────────────────

        private bool CanSwitchToSelectedServer()
        {
            return ControlPanel.IsRunning
                && !ControlPanel.IsReapplying
                && ServerList.SelectedServer is not null
                && ServerList.CanRunSelectedServer
                && !ReferenceEquals(ServerList.SelectedServer, _activeServer);
        }

        [RelayCommand(CanExecute = nameof(CanSwitchToSelectedServer))]
        private async Task SwitchToSelectedServer()
        {
            if (!CanSwitchToSelectedServer()) return;

            await ControlPanel.SwitchToSelectedServerAsync();
        }

        private void OpenPersonalize()
        {
            Personalize.LoadFromStore();
            _showPersonalize = true;
            OnPropertyChanged(nameof(MainContentVisibility));
            OnPropertyChanged(nameof(PersonalizeVisibility));
            OnPropertyChanged(nameof(BackButtonVisibility));
        }

        private void ClosePersonalize()
        {
            _showPersonalize = false;
            OnPropertyChanged(nameof(MainContentVisibility));
            OnPropertyChanged(nameof(PersonalizeVisibility));
            OnPropertyChanged(nameof(BackButtonVisibility));
        }

        // ── Back navigation (TitleBar back button) ────────────────────────────
        // Discards any in-flight edits and returns to the main view without saving.

        [RelayCommand]
        private void GoBack()
        {
            if (!_showPersonalize) return;
            ClosePersonalize();
        }

        // ── Property change wiring ─────────────────────────────────────────────

        private void OnServerListPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServerListViewModel.SelectedServer))
            {
                ServerDetail.SelectedServer = ServerList.SelectedServer;
                OnPropertyChanged(nameof(ActiveServerName));
                OnPropertyChanged(nameof(TrayTooltip));
                ControlPanel.NotifyStartStopStateChanged();
                SwitchToSelectedServerCommand.NotifyCanExecuteChanged();
            }
            else if (e.PropertyName == nameof(ServerListViewModel.CanRunSelectedServer))
            {
                ControlPanel.NotifyStartStopStateChanged();
                SwitchToSelectedServerCommand.NotifyCanExecuteChanged();
            }
        }

        private async void OnPresetImported(object? sender, System.EventArgs e)
        {
            try
            {
                ServerList.SelectedServer = null;
                ServerList.Servers.Clear();
                await ServerList.LoadServersAsync();
                ServerDetail.SelectedServer = ServerList.SelectedServer;
                // Belt-and-suspenders against any old servers.json on disk that still
                // carries IsActive=true from before ServerEntry.IsActive got JsonIgnore.
                ClearActiveServerFlags();
                // Old _activeServer references a ServerEntry no longer in the list; even if
                // xray is still running with the old config, the new SelectedServer is not
                // logically active. Clear so the UI doesn't claim a stale Active state.
                UpdateActiveServer(null);
                ServerList.IsProxyRunning = ControlPanel.IsRunning;
                OnPropertyChanged(nameof(ActiveServerName));
                OnPropertyChanged(nameof(TrayTooltip));
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] OnPresetImported failed: {ex}");
            }
        }

        private void OnPersonalizePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersonalizeViewModel.ShowLatencyInDetails))
            {
                ServerDetail.ShowLatencyInDetails = Personalize.ShowLatencyInDetails;
            }
            else if (e.PropertyName == nameof(PersonalizeViewModel.ShowAiUnlockInDetails))
            {
                ServerDetail.ShowAiUnlockInDetails = Personalize.ShowAiUnlockInDetails;
            }
            else if (e.PropertyName == nameof(PersonalizeViewModel.ShowGroupInDetails))
            {
                ServerDetail.ShowGroupInDetails = Personalize.ShowGroupInDetails;
            }
            else if (e.PropertyName == nameof(PersonalizeViewModel.OpenServerFilterPanelOnStartup))
            {
                ServerList.IsFilterPanelOpen = Personalize.OpenServerFilterPanelOnStartup;
            }
        }

        private void OnServerDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServerDetailViewModel.LatencyText)
                && ControlPanel.IsRunning
                && ReferenceEquals(ServerDetail.SelectedServer, _activeServer))
            {
                _activeLatencyText = ServerDetail.LatencyText;
                OnPropertyChanged(nameof(MiniStatusText));
            }
        }

        private void OnControlPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ControlPanelViewModel.IsReapplying))
            {
                SwitchToSelectedServerCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(TrayTooltip));
                return;
            }

            if (e.PropertyName == nameof(ControlPanelViewModel.RoutingMode))
            {
                OnPropertyChanged(nameof(MiniRoutingMode));
                return;
            }

            if (e.PropertyName != nameof(ControlPanelViewModel.IsRunning)) return;

            var isRunning = ControlPanel.IsRunning;
            UpdateActiveServer(isRunning ? ServerList.SelectedServer : null);
            ServerList.IsProxyRunning = isRunning;
            OnPropertyChanged(nameof(ActiveServerName));
            OnPropertyChanged(nameof(TrayTooltip));
            OnPropertyChanged(nameof(MiniIsRunning));
            OnPropertyChanged(nameof(MiniStatusText));
            OnPropertyChanged(nameof(MiniDotVisibility));
            SwitchToSelectedServerCommand.NotifyCanExecuteChanged();

            ServerDetail.OnProxyRunningChanged(isRunning, ControlPanel.LocalPort);

            if (isRunning && !ControlPanel.IsUpdateAvailable)
                QueueUpdateCheck(CurrentProxyUrl());

            // Scheduled refreshes stand down while the proxy is down, so connecting is the moment
            // an overdue subscription becomes fetchable. Without this it would sit until the next
            // minute tick — a visible lag right after the user connects and opens the list.
            if (isRunning)
                _ = RunSubscriptionRefreshCheckAsync();
        }

        private void UpdateActiveServer(ServerEntry? server)
        {
            var previous = _activeServer;
            if (ReferenceEquals(previous, server))
            {
                _activeLatencyText = server is not null ? ServerDetail.LatencyText : string.Empty;
                ServerDetail.ActiveServer = server;
                if (server is not null)
                    server.IsActive = true;
                return;
            }

            if (previous is not null)
                previous.IsActive = false;

            _activeServer = server;
            _activeLatencyText = server is not null ? ServerDetail.LatencyText : string.Empty;
            ServerDetail.ActiveServer = server;

            if (server is not null)
                server.IsActive = true;
        }

        private void ClearActiveServerFlags()
        {
            foreach (var item in ServerList.Servers)
                item.IsActive = false;
        }
    }
}

