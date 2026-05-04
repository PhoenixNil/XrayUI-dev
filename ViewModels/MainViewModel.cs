using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class MainViewModel : BaseViewModel
    {
        private readonly SettingsService _settings;
        private readonly StartupService _startupService;
        private readonly IUpdateService _updateService;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;
        private bool _updateCheckQueued;
        private ServerEntry? _activeServer;
        private string _activeLatencyText = string.Empty;
        private bool _showPersonalize;
        private bool _isMiniMode;

        public ServerListViewModel   ServerList   { get; }
        public ServerDetailViewModel ServerDetail { get; }
        public ControlPanelViewModel ControlPanel { get; }
        public PersonalizeViewModel  Personalize  { get; }

        public Visibility MainContentVisibility => _showPersonalize ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PersonalizeVisibility  => _showPersonalize ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility BackButtonVisibility   => _showPersonalize ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility MiniModeVisibility     => _isMiniMode      ? Visibility.Visible   : Visibility.Collapsed;
        public Visibility FullModeVisibility     => _isMiniMode      ? Visibility.Collapsed : Visibility.Visible;

        public bool IsMiniMode
        {
            get => _isMiniMode;
            set
            {
                if (SetProperty(ref _isMiniMode, value))
                {
                    OnPropertyChanged(nameof(MiniModeVisibility));
                    OnPropertyChanged(nameof(FullModeVisibility));
                }
            }
        }

        public string ActiveServerName =>
            (ControlPanel.IsRunning ? _activeServer : ServerList.SelectedServer)?.Name ?? "未选择";

        public string MiniStatusText => ControlPanel.IsRunning ? _activeLatencyText : "未连接";
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
            var aiUnlockCheck = new AiUnlockCheckService();

            Title = "Proxy Console";

            ServerList   = new ServerListViewModel(dialogs, settings);
            ServerDetail = new ServerDetailViewModel(latencyProbe, aiUnlockCheck);
            ControlPanel = new ControlPanelViewModel(dialogs, settings, xray, tunService, startupService, updateService);
            Personalize  = new PersonalizeViewModel(settings);

            // Wire ControlPanel so it knows the current selected server
            ControlPanel.GetSelectedServer = () => ServerList.SelectedServer;

            ServerList.PropertyChanged   += OnServerListPropertyChanged;
            ControlPanel.PropertyChanged += OnControlPanelPropertyChanged;
            ServerDetail.PropertyChanged += OnServerDetailPropertyChanged;
            Personalize.PropertyChanged  += OnPersonalizePropertyChanged;

            ControlPanel.ShowPersonalizeRequested += (_, _) => OpenPersonalize();
            Personalize.CloseRequested            += (_, _) => ClosePersonalize();

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
            UpdateActiveServer(null);
            ServerList.IsProxyRunning = ControlPanel.IsRunning;

            // Load settings and apply to ControlPanel
            var s = await _settings.LoadSettingsAsync();
            ControlPanel.LocalPort             = s.LocalMixedPort;
            ControlPanel.RoutingMode           = s.RoutingMode == "global" ? "全局路由" : "智能分流";
            ControlPanel.IsSystemProxyEnabled  = s.IsSystemProxyEnabled;
            ControlPanel.InitializePersonalize(s);
            Personalize.LoadDisplayOptions(s);
            ApplyDetailDisplayOptions(s.ShowLatencyInDetails, s.ShowAiUnlockInDetails);

            // Reconcile external state vs persisted setting (external is ground truth)
            var externalEnabled = _startupService.IsStartupEnabled();
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

            // Fire-and-forget background tasks. Failures here must never block
            // startup or surface as dialogs (per the auto-update failure policy).
            _ = Task.Run(() => _updateService.CleanupOldStagingDirs());
            QueueUpdateCheck(CurrentProxyUrl());
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
            await ControlPanel.StartStopCommand.ExecuteAsync(null);
        }

        // ── Personalize navigation ────────────────────────────────────────────

        private bool CanSwitchToSelectedServer()
        {
            return ControlPanel.IsRunning
                && !ControlPanel.IsReapplying
                && ServerList.SelectedServer is not null
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

        private void ApplyDetailDisplayOptions(bool showLatency, bool showAiUnlock)
        {
            ServerDetail.ShowLatencyInDetails = showLatency;
            ServerDetail.ShowAiUnlockInDetails = showAiUnlock;
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
                SwitchToSelectedServerCommand.NotifyCanExecuteChanged();
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
                return;
            }

            if (e.PropertyName != nameof(ControlPanelViewModel.IsRunning)) return;

            var isRunning = ControlPanel.IsRunning;
            UpdateActiveServer(isRunning ? ServerList.SelectedServer : null);
            ServerList.IsProxyRunning = isRunning;
            OnPropertyChanged(nameof(ActiveServerName));
            OnPropertyChanged(nameof(MiniStatusText));
            OnPropertyChanged(nameof(MiniDotVisibility));
            SwitchToSelectedServerCommand.NotifyCanExecuteChanged();

            ServerDetail.OnProxyRunningChanged(isRunning, ControlPanel.LocalPort);

            if (isRunning && !ControlPanel.IsUpdateAvailable)
                QueueUpdateCheck(CurrentProxyUrl());
        }

        private void UpdateActiveServer(ServerEntry? server)
        {
            _activeServer = server;
            _activeLatencyText = server is not null ? ServerDetail.LatencyText : string.Empty;
            ServerDetail.ActiveServer = _activeServer;

            foreach (var item in ServerList.Servers)
            {
                item.IsActive = ReferenceEquals(item, _activeServer);
            }
        }
    }
}

