using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.ViewModels
{
    /// <summary>
    /// The panel a left click on the tray icon opens: connection state, routing mode and the two
    /// proxy switches, reachable without bringing the main window back.
    ///
    /// The panel is rebuilt on every open and this VM is disposed when it closes. All of its
    /// bindings go through this object instead of straight onto ControlPanelViewModel, so every
    /// subscription into the long-lived view models is owned here and dropped in
    /// <see cref="Dispose"/>. Bound directly, x:Bind's change trackers would stay subscribed to
    /// them after each panel closed, until a later change found the panel collected.
    /// </summary>
    public sealed partial class TrayPanelViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel _main;
        private readonly Action _openMainWindow;
        private readonly Action _exitApplication;
        private readonly DispatcherQueue _dispatcher;

        // The node the panel describes (MainViewModel.DisplayedServer), watched for a rename or a
        // protocol change.
        private ServerEntry? _server;

        /// <param name="openMainWindow">Brings the main window back the way a double-click does.</param>
        /// <param name="exitApplication">Quits the app, as the tray menu's exit item does.</param>
        public TrayPanelViewModel(MainViewModel main, Action openMainWindow, Action exitApplication)
        {
            _main            = main;
            _openMainWindow  = openMainWindow;
            _exitApplication = exitApplication;
            _dispatcher      = DispatcherQueue.GetForCurrentThread();

            _main.PropertyChanged += OnMainPropertyChanged;
            Panel.PropertyChanged += OnPanelPropertyChanged;
            TrackServer();
        }

        private ControlPanelViewModel Panel => _main.ControlPanel;

        // ── Header ────────────────────────────────────────────────────────────

        /// <summary>Picks the green app icon, on the same terms as the tray icon: a node switch
        /// (IsReapplying) keeps it green rather than flashing it blue.</summary>
        public bool ShowsRunning => _main.TrayShowsRunning;

        public bool IsConnected => Panel.IsRunning;

        private bool CanToggleConnection() => Panel.CanStartStop;

        [RelayCommand(CanExecute = nameof(CanToggleConnection))]
        private Task ToggleConnectionAsync() => Panel.StartStopCommand.ExecuteAsync(null);

        [RelayCommand]
        private void OpenMainWindow() => _openMainWindow();

        [RelayCommand]
        private void ExitApplication() => _exitApplication();

        // ── Node and status ───────────────────────────────────────────────────

        public string ServerName => _main.ActiveServerName;

        public string ProtocolText => _server?.DisplayProtocol ?? string.Empty;

        // The code, coloured in the view by ProtocolToBrushConverter as in the server list.
        public string? Protocol => _server?.Protocol;

        // With no node selected there is no protocol to caption the name with, so a plain
        // label takes its place and the row keeps its shape.
        public Visibility ProtocolVisibility => _server is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility NodeLabelVisibility => _server is null ? Visibility.Visible : Visibility.Collapsed;

        public string StatusText =>
            Panel.IsReapplying ? L.ControlPanel_StatusApplying :
            Panel.IsRunning    ? L.TrayPanel_Connected :
                                 L.Main_NotConnected;

        public bool IsBusy => Panel.IsReapplying;

        public Visibility ConnectedDotVisibility =>
            Panel.IsRunning && !Panel.IsReapplying ? Visibility.Visible : Visibility.Collapsed;

        public Visibility IdleDotVisibility =>
            !Panel.IsRunning && !Panel.IsReapplying ? Visibility.Visible : Visibility.Collapsed;

        // ── Routing ───────────────────────────────────────────────────────────

        // Both false while a config profile owns routing, so neither built-in mode reads as the
        // one in effect.
        public bool IsSmartRouting => !Panel.IsCustomConfigActive && Panel.RoutingMode != "global";

        public bool IsGlobalRouting => !Panel.IsCustomConfigActive && Panel.RoutingMode == "global";

        public bool CanChangeRouting => Panel.IsBuiltInConfigEnabled;

        [RelayCommand]
        private void SelectRouting(string mode)
        {
            if (mode == Panel.RoutingMode || !CanChangeRouting)
            {
                // A ToggleButton unchecks itself when clicked again; put the real state back.
                NotifyRoutingChanged();
                return;
            }

            Defer(() => _ = Panel.SetRoutingModeCommand.ExecuteAsync(mode));
        }

        // ── Switches ──────────────────────────────────────────────────────────

        public bool IsSystemProxyOn
        {
            get => Panel.IsSystemProxyEnabled;
            set
            {
                if (value == Panel.IsSystemProxyEnabled || !CanToggleSystemProxy) return;
                Defer(() => _ = Panel.SetProxyModeCommand.ExecuteAsync(value ? "system" : "manual"));
            }
        }

        public bool CanToggleSystemProxy => Panel.IsModeToggleEnabled;

        public bool IsTunOn
        {
            get => Panel.IsTunMode;
            set
            {
                if (value == Panel.IsTunMode || !CanToggleTun) return;
                // Unelevated, turning TUN on raises the elevation prompt; MainWindow closes the
                // panel and brings itself back to host it.
                Defer(() => Panel.IsTunMode = value);
            }
        }

        public bool CanToggleTun => Panel.IsTunToggleEnabled;

        // ── Change tracking ───────────────────────────────────────────────────

        private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.ActiveServerName):
                    OnPropertyChanged(nameof(ServerName));
                    break;
                case nameof(MainViewModel.DisplayedServer):
                    TrackServer();
                    break;
            }
        }

        private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ControlPanelViewModel.IsRunning):
                    OnPropertyChanged(nameof(IsConnected));
                    NotifyStatusChanged();
                    break;
                case nameof(ControlPanelViewModel.IsReapplying):
                    OnPropertyChanged(nameof(IsBusy));
                    NotifyStatusChanged();
                    break;
                case nameof(ControlPanelViewModel.CanStartStop):
                    ToggleConnectionCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(ControlPanelViewModel.RoutingMode):
                case nameof(ControlPanelViewModel.IsCustomConfigActive):
                    NotifyRoutingChanged();
                    break;
                case nameof(ControlPanelViewModel.IsBuiltInConfigEnabled):
                    OnPropertyChanged(nameof(CanChangeRouting));
                    break;
                case nameof(ControlPanelViewModel.IsSystemProxyEnabled):
                    OnPropertyChanged(nameof(IsSystemProxyOn));
                    break;
                case nameof(ControlPanelViewModel.IsModeToggleEnabled):
                    OnPropertyChanged(nameof(CanToggleSystemProxy));
                    break;
                case nameof(ControlPanelViewModel.IsTunMode):
                    OnPropertyChanged(nameof(IsTunOn));
                    break;
                case nameof(ControlPanelViewModel.IsTunToggleEnabled):
                    OnPropertyChanged(nameof(CanToggleTun));
                    break;
            }
        }

        private void OnServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                // ActiveServerName reads the name off this entry but is not raised on a rename.
                case nameof(ServerEntry.Name):
                    OnPropertyChanged(nameof(ServerName));
                    break;
                // Also what RefreshProtocolColor raises after a palette edit.
                case nameof(ServerEntry.Protocol):
                    OnPropertyChanged(nameof(ProtocolText));
                    OnPropertyChanged(nameof(Protocol));
                    break;
            }
        }

        private void TrackServer()
        {
            var server = _main.DisplayedServer;
            if (ReferenceEquals(server, _server)) return;

            if (_server is not null) _server.PropertyChanged -= OnServerPropertyChanged;
            _server = server;
            if (_server is not null) _server.PropertyChanged += OnServerPropertyChanged;

            OnPropertyChanged(nameof(ProtocolText));
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(ProtocolVisibility));
            OnPropertyChanged(nameof(NodeLabelVisibility));
        }

        private void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(ShowsRunning));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectedDotVisibility));
            OnPropertyChanged(nameof(IdleDotVisibility));
        }

        private void NotifyRoutingChanged()
        {
            OnPropertyChanged(nameof(IsSmartRouting));
            OnPropertyChanged(nameof(IsGlobalRouting));
        }

        /// <summary>
        /// Runs a command once the binding update that requested it has unwound. The commands
        /// raise the very properties the requesting control is bound to — TUN even reverts its
        /// own toggle synchronously — and deferring keeps that echo from re-entering a control
        /// that is still raising its own change.
        /// </summary>
        private void Defer(DispatcherQueueHandler action) => _dispatcher.TryEnqueue(action);

        public void Dispose()
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
            Panel.PropertyChanged -= OnPanelPropertyChanged;
            if (_server is not null) _server.PropertyChanged -= OnServerPropertyChanged;
            _server = null;
        }
    }
}
