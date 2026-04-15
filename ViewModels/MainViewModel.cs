using System.ComponentModel;
using System.Threading.Tasks;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class MainViewModel : BaseViewModel
    {
        private readonly SettingsService _settings;
        private ServerEntry? _activeServer;

        public ServerListViewModel   ServerList   { get; }
        public ServerDetailViewModel ServerDetail { get; }
        public ControlPanelViewModel ControlPanel { get; }

        public MainViewModel(
            IDialogService  dialogs,
            SettingsService settings,
            XrayService     xray,
            TunService      tunService)
        {
            _settings = settings;
            var latencyProbe = new LatencyProbeService(
                new TcpConnectProbeService(),
                new PingProbeService());
            var aiUnlockCheck = new AiUnlockCheckService();

            Title = "Proxy Console";

            ServerList   = new ServerListViewModel(dialogs, settings);
            ServerDetail = new ServerDetailViewModel(latencyProbe, aiUnlockCheck);
            ControlPanel = new ControlPanelViewModel(dialogs, settings, xray, tunService);

            // Wire ControlPanel so it knows the current selected server
            ControlPanel.GetSelectedServer = () => ServerList.SelectedServer;

            ServerList.PropertyChanged   += OnServerListPropertyChanged;
            ControlPanel.PropertyChanged += OnControlPanelPropertyChanged;

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

            // Load settings and apply to ControlPanel
            var s = await _settings.LoadSettingsAsync();
            ControlPanel.LocalPort    = s.LocalSocksPort;
            ControlPanel.RoutingMode  = s.RoutingMode == "global" ? "全局路由" : "智能分流";
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

