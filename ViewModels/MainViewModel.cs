using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class MainViewModel : BaseViewModel
    {
        private readonly SettingsService _settings;
        private readonly StartupService _startupService;
        private ServerEntry? _activeServer;
        private bool _showPersonalize;

        public ServerListViewModel   ServerList   { get; }
        public ServerDetailViewModel ServerDetail { get; }
        public ControlPanelViewModel ControlPanel { get; }
        public PersonalizeViewModel  Personalize  { get; }

        public Visibility MainContentVisibility => _showPersonalize ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PersonalizeVisibility  => _showPersonalize ? Visibility.Visible   : Visibility.Collapsed;
        public bool       IsBackButtonVisible    => _showPersonalize;

        public MainViewModel(
            IDialogService  dialogs,
            SettingsService settings,
            XrayService     xray,
            TunService      tunService,
            StartupService  startupService)
        {
            _settings       = settings;
            _startupService = startupService;
            var latencyProbe = new LatencyProbeService(
                new TcpConnectProbeService(),
                new PingProbeService());
            var aiUnlockCheck = new AiUnlockCheckService();

            Title = "Proxy Console";

            ServerList   = new ServerListViewModel(dialogs, settings);
            ServerDetail = new ServerDetailViewModel(latencyProbe, aiUnlockCheck);
            ControlPanel = new ControlPanelViewModel(dialogs, settings, xray, tunService, startupService);
            Personalize  = new PersonalizeViewModel(settings);

            // Wire ControlPanel so it knows the current selected server
            ControlPanel.GetSelectedServer = () => ServerList.SelectedServer;

            ServerList.PropertyChanged   += OnServerListPropertyChanged;
            ControlPanel.PropertyChanged += OnControlPanelPropertyChanged;

            ControlPanel.ShowPersonalizeRequested += (_, _) => OpenPersonalize();
            Personalize.CloseRequested            += (_, _) => ClosePersonalize();

            ServerDetail.SelectedServer = ServerList.SelectedServer;
        }

        // ── Startup initialisation (call after Window is ready) ───────────────

        public async Task InitializeAsync()
        {
            // Load saved server list
            await ServerList.LoadServersAsync();

            // Sync ServerDetail with whatever was selected
            ServerDetail.SelectedServer = ServerList.SelectedServer;
            UpdateActiveServer(null);
            ServerList.IsProxyRunning = ControlPanel.IsRunning;

            // Load settings and apply to ControlPanel
            var s = await _settings.LoadSettingsAsync();
            ControlPanel.LocalPort    = s.LocalSocksPort;
            ControlPanel.RoutingMode  = s.RoutingMode == "global" ? "全局路由" : "智能分流";
            ControlPanel.InitializePersonalize(s);

            // Reconcile registry vs persisted setting (registry is ground truth)
            var registryEnabled = _startupService.IsStartupEnabled();
            if (s.IsStartupEnabled != registryEnabled)
            {
                s.IsStartupEnabled = registryEnabled;
                await _settings.SaveSettingsAsync(s);
            }
            ControlPanel.IsStartupEnabled = s.IsStartupEnabled;
            ControlPanel.IsAutoConnect    = s.IsAutoConnect;

            if (s.IsStartupEnabled && s.IsAutoConnect)
                await TryAutoConnectAsync(s);
        }

        private async Task TryAutoConnectAsync(AppSettings s)
        {
            var target = (!string.IsNullOrEmpty(s.LastAutoConnectServerName)
                ? ServerList.Servers.FirstOrDefault(
                    x => string.Equals(x.Name, s.LastAutoConnectServerName, System.StringComparison.OrdinalIgnoreCase))
                : null)
                ?? ServerList.Servers.FirstOrDefault();

            if (target is null) return;
            ServerList.SelectedServer = target;
            await ControlPanel.StartStopCommand.ExecuteAsync(null);
        }

        // ── Personalize navigation ────────────────────────────────────────────

        private void OpenPersonalize()
        {
            Personalize.LoadFromStore();
            _showPersonalize = true;
            OnPropertyChanged(nameof(MainContentVisibility));
            OnPropertyChanged(nameof(PersonalizeVisibility));
            OnPropertyChanged(nameof(IsBackButtonVisible));
        }

        private void ClosePersonalize()
        {
            _showPersonalize = false;
            OnPropertyChanged(nameof(MainContentVisibility));
            OnPropertyChanged(nameof(PersonalizeVisibility));
            OnPropertyChanged(nameof(IsBackButtonVisible));
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
                ServerDetail.SelectedServer = ServerList.SelectedServer;
        }

        private void OnControlPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ControlPanelViewModel.IsRunning)) return;

            var isRunning = ControlPanel.IsRunning;
            UpdateActiveServer(isRunning ? ServerList.SelectedServer : null);
            ServerList.IsProxyRunning = isRunning;

            // Trigger AI unlock detection through the local HTTP proxy
            var httpPort = ControlPanel.LocalPort + 1; // HTTP proxy = SOCKS + 1
            ServerDetail.OnProxyRunningChanged(isRunning, httpPort);
        }

        private void UpdateActiveServer(ServerEntry? server)
        {
            _activeServer = server;
            ServerDetail.ActiveServer = _activeServer;

            foreach (var item in ServerList.Servers)
            {
                item.IsActive = ReferenceEquals(item, _activeServer);
            }
        }
    }
}

