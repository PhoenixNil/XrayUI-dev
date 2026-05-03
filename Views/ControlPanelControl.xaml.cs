using System;
using Windows.System;

namespace XrayUI.Views
{
    public sealed partial class ControlPanelControl
    {
        private LogWindow? _logWindow;
        private CustomRulesWindow? _customRulesWindow;

        public ControlPanelViewModel ViewModel { get; set; } = null!;

        public ControlPanelControl()
        {
            this.InitializeComponent();
        }

        // Called by MainWindow after ViewModel is assigned (via x:Bind the property is set before Loaded)
        // We wire the event in the Loaded handler to be safe.
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowLogsRequested         += OnShowLogsRequested;
            ViewModel.ShowCustomRulesRequested  += OnShowCustomRulesRequested;
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowLogsRequested         -= OnShowLogsRequested;
            ViewModel.ShowCustomRulesRequested  -= OnShowCustomRulesRequested;
        }

        private async void GitHubButton_Click(object sender, RoutedEventArgs e)
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/PhoenixNil/XrayUI-dev"));
        }

        public void CloseLogWindow()
        {
            var logWindow = _logWindow;
            if (logWindow is null)
            {
                return;
            }

            _logWindow = null;
            logWindow.Close();
        }

        public void CloseCustomRulesWindow()
        {
            var w = _customRulesWindow;
            if (w is null)
            {
                return;
            }

            _customRulesWindow = null;
            w.Close();
        }

        private void OnShowLogsRequested(object? sender, EventArgs e)
        {
            if (_logWindow is null)
            {
                _logWindow = new LogWindow(
                    ViewModel.XrayService,
                    ViewModel.SettingsService,
                    ViewModel.ReapplyRoutingAsync);
                _logWindow.Closed += (_, _) => _logWindow = null;
            }
            _logWindow.Activate();
        }

        private void OnShowCustomRulesRequested(object? sender, CustomRulesViewModel vm)
        {
            if (_customRulesWindow is null)
            {
                if ((Application.Current as App)?.Window is not { } mainWindow)
                    return;

                _customRulesWindow = new CustomRulesWindow(mainWindow, vm);
                _customRulesWindow.Closed += (_, _) => _customRulesWindow = null;
            }
            _customRulesWindow.Activate();
        }
    }
}
