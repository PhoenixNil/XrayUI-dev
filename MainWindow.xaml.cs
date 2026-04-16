using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI
{
    public sealed partial class MainWindow
    {
        private readonly FrameworkElement _rootElement;
        private readonly WindowManager _windowManager;
        private bool _allowClose;
        private bool _initialized;
        private bool _isHiddenToTray;

        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            // Build services before InitializeComponent so ViewModel is ready for x:Bind
            var settingsService = new SettingsService();
            var xrayService = new XrayService();
            var tunService = new TunService();
            var dialogService = new DialogService(() => _initialized ? Content?.XamlRoot : null);

            ViewModel = new MainViewModel(dialogService, settingsService, xrayService, tunService);

            InitializeComponent();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetWindowScale(hWnd);
            AppWindow.Resize(new SizeInt32((int)Math.Round(950 * scale), (int)Math.Round(600 * scale)));
            _windowManager = WindowManager.Get(this);

            _rootElement = (FrameworkElement)Content;
            ThemeHelper.RootElement = _rootElement;
            _rootElement.ActualThemeChanged += OnRootElementActualThemeChanged;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            ConfigureTray();

            UpdateCaptionButtonColors();

            Activated += OnFirstActivated;
            Closed += OnClosed;
        }

        private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            _initialized = true;
            await ViewModel.InitializeAsync();
        }

        private void ConfigureTray()
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "output.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            _windowManager.IsVisibleInTray = true;
            _windowManager.TrayIconSelected += (_, _) => RestoreFromTray();
            _windowManager.TrayIconContextMenu += (_, e) =>
            {
                var flyout = new MenuFlyout();

                var openItem = new MenuFlyoutItem { Text = "\u6253\u5f00\u7a97\u53e3" };
                openItem.Click += (_, _) => RestoreFromTray();
                flyout.Items.Add(openItem);

                flyout.Items.Add(new MenuFlyoutSeparator());

                var exitItem = new MenuFlyoutItem { Text = "\u9000\u51fa" };
                exitItem.Click += (_, _) => ExitApplication();
                flyout.Items.Add(exitItem);

                e.Flyout = flyout;
            };
            AppWindow.Closing += (_, args) =>
            {
                if (_allowClose)
                {
                    return;
                }

                args.Cancel = true;
                HideToTray();
            };
        }

        private void HideToTray()
        {
            if (_isHiddenToTray)
            {
                return;
            }

            _isHiddenToTray = true;
            ControlPanel.CloseLogWindow();

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();

            ReleaseUiResources();
        }

        private void RestoreFromTray()
        {
            _isHiddenToTray = false;
            AppWindow.IsShownInSwitchers = true;
            Activate();
        }

        private void ExitApplication()
        {
            if (_allowClose)
            {
                return;
            }

            _allowClose = true;
            _isHiddenToTray = false;
            try
            {
                _windowManager.IsVisibleInTray = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Failed to hide tray icon during exit: {ex.Message}");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);

                try
                {
                    if (Microsoft.UI.Xaml.Application.Current is App app)
                    {
                        app.RequestShutdown();
                        return;
                    }

                    StopBackgroundServicesOnExit();
                    SystemProxyService.ClearProxy();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Tray] RequestShutdown failed: {ex}");
                }

                Environment.Exit(0);
            });
        }

        private void OnRootElementActualThemeChanged(FrameworkElement sender, object args)
        {
            UpdateCaptionButtonColors();
        }

        private void UpdateCaptionButtonColors()
        {
            var tb = AppWindow.TitleBar;
            var isDarkTheme = _rootElement.ActualTheme == ElementTheme.Dark;

            var foregroundColor = isDarkTheme
                ? Colors.White
                : Color.FromArgb(230, 0, 0, 0);
            var inactiveForegroundColor = isDarkTheme
                ? Color.FromArgb(153, 255, 255, 255)
                : Color.FromArgb(138, 0, 0, 0);
            var hoverBackgroundColor = isDarkTheme
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(18, 0, 0, 0);
            var pressedBackgroundColor = isDarkTheme
                ? Color.FromArgb(60, 255, 255, 255)
                : Color.FromArgb(36, 0, 0, 0);

            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = hoverBackgroundColor;
            tb.ButtonPressedBackgroundColor = pressedBackgroundColor;
            tb.ButtonForegroundColor = foregroundColor;
            tb.ButtonInactiveForegroundColor = inactiveForegroundColor;
            tb.ButtonHoverForegroundColor = foregroundColor;
            tb.ButtonPressedForegroundColor = foregroundColor;
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _rootElement.ActualThemeChanged -= OnRootElementActualThemeChanged;
            AppWindow.IsShownInSwitchers = true;
        }

        private static void ReleaseUiResources()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                using var process = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Failed to release UI resources: {ex.Message}");
            }
        }

        public void StopBackgroundServicesOnExit()
        {
            ViewModel.ControlPanel.CleanupTunOnExit();
            ViewModel.ControlPanel.XrayService.StopForShutdown();
        }

        private static double GetWindowScale(IntPtr hwnd)
        {
            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                {
                    var dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0) return dpi / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        [DllImport("user32.dll")]
        private static extern int GetDpiForWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process,
            IntPtr minimumWorkingSetSize,
            IntPtr maximumWorkingSetSize);
    }
}
