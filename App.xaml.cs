using System;
using System.Linq;
using XrayUI.Services;

namespace XrayUI
{
    public partial class App
    {
        private Window? _window;
        private bool _cleanupStarted;

        public App()
        {
            this.InitializeComponent();

            this.UnhandledException += (_, _) => CleanupOnExit();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOnExit();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => CleanupOnExit();

            // 检测 --tun 参数：以管理员身份重启后自动开启 TUN 模式
            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Contains("--tun", StringComparer.OrdinalIgnoreCase))
            {
                if (_window is MainWindow mw)
                    mw.ViewModel.ControlPanel.SetTunEnabledSilently(true);
            }

            _window.Activate();
        }

        private void CleanupOnExit()
        {
            if (_cleanupStarted)
            {
                return;
            }

            _cleanupStarted = true;

            if (_window is MainWindow mainWindow)
            {
                mainWindow.StopBackgroundServicesOnExit();
            }

            SystemProxyService.ClearProxy();
        }
    }
}