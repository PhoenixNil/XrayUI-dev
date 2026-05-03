using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;
using WinUIEx.Messaging;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI
{
    public sealed partial class MainWindow
    {
        private readonly FrameworkElement _rootElement;
        private readonly Border _miniDragRegion;
        private readonly Button _miniExpandButton;
        private readonly WindowManager _windowManager;
        private readonly WindowMessageMonitor _windowMessageMonitor;
        private bool _isSessionEnding;
        private bool _allowClose;
        private bool _initialized;
        private bool _isHiddenToTray;
        private readonly bool _startMinimized;
        // Set when we parked the window off-screen at startup; cleared after
        // we re-center it on the first user-initiated show (tray click).
        private bool _needsCenterOnFirstShow;

        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;
        private const uint WmNclButtonDown   = 0x00A1;
        private const uint WmNclButtonDblClk = 0x00A3;
        private const int HtCaption = 0x0002;
        private const int FullWindowWidth = 950;
        private const int FullWindowHeight = 600;
        private const int MiniWindowWidth = 330;
        private const int MiniWindowHeight = 136;

        public MainViewModel ViewModel { get; }

        public MainWindow(bool startMinimized = false)
        {
            _startMinimized           = startMinimized;
            _needsCenterOnFirstShow   = startMinimized;

            // Build services before InitializeComponent so ViewModel is ready for x:Bind
            var settingsService = new SettingsService();
            var xrayService     = new XrayService();
            var tunService      = new TunService();
            var startupService  = new StartupService();
            var dialogService   = new DialogService(() => _initialized ? Content?.XamlRoot : null);
            var updateService   = new UpdateService();

            ViewModel = new MainViewModel(dialogService, settingsService, xrayService, tunService, startupService, updateService);

            InitializeComponent();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetWindowScale(hWnd);
            AppWindow.Resize(new SizeInt32((int)Math.Round(950 * scale), (int)Math.Round(600 * scale)));
            _windowManager = WindowManager.Get(this);
            _windowMessageMonitor = new WindowMessageMonitor(this);
            _windowMessageMonitor.WindowMessageReceived += OnWindowMessageReceived;

            _rootElement = (FrameworkElement)Content;
            ThemeHelper.RootElement = _rootElement;
            ThemeHelper.MainWindow = this;
            ThemeHelper.ApplyBackdrop("Mica");
            _rootElement.ActualThemeChanged += OnRootElementActualThemeChanged;
            _miniDragRegion = (Border)_rootElement.FindName("MiniDragRegion");
            _miniExpandButton = (Button)_rootElement.FindName("MiniExpandButton");

            _miniDragRegion.PointerPressed += MiniDragRegion_PointerPressed;
            _miniExpandButton.Click += MiniExpandButton_Click;

            var miniCloseButton = (Button)_rootElement.FindName("MinicloseButton");
            miniCloseButton.Click += (_, _) => HideToTray();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            ConfigureTray();

            ApplyWindowMode(isMini: false);
            UpdateCaptionButtonColors();

            Activated += OnFirstActivated;
            Closed += OnClosed;
        }

        private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            _initialized = true;

            // For --startup-minimized we only hide the window here so the XamlRoot
            // stays alive for any dialogs raised during InitializeAsync (e.g.
            // auto-connect errors). The full tray transition (ReleaseUiResources)
            // runs after init so resources are still freed in the minimized case.
            if (_startMinimized)
            {
                AppWindow.IsShownInSwitchers = false;
                AppWindow.Hide();
            }

            await ViewModel.InitializeAsync(isBootLaunch: _startMinimized);

            if (_startMinimized) HideToTray();
        }

        private void AppTitleBar_BackRequested(object sender, RoutedEventArgs e)
        {
            if (ViewModel.GoBackCommand.CanExecute(null))
            {
                ViewModel.GoBackCommand.Execute(null);
            }
        }

        private void DockButton_Click(object sender, RoutedEventArgs e)
        {
            SetMiniMode(!ViewModel.IsMiniMode);
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
                if (_allowClose || _isSessionEnding)
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
            ControlPanel?.CloseLogWindow();
            ControlPanel?.CloseCustomRulesWindow();

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();

            ReleaseUiResources();
        }

        internal void RestoreFromTray()
        {
            _isHiddenToTray = false;
            AppWindow.IsShownInSwitchers = true;

            if (_needsCenterOnFirstShow)
            {
                _needsCenterOnFirstShow = false;
                CenterOnPrimaryDisplay();
            }

            Activate();
        }

        private void CenterOnPrimaryDisplay()
        {
            var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var size = AppWindow.Size;
            var x = workArea.X + (workArea.Width  - size.Width)  / 2;
            var y = workArea.Y + (workArea.Height - size.Height) / 2;
            AppWindow.Move(new PointInt32(x, y));
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

                    SystemProxyService.ClearProxy();
                    StopBackgroundServicesOnExit();
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

        private void SetMiniMode(bool isMini)
        {
            ViewModel.IsMiniMode = isMini;
            ApplyWindowMode(isMini);
        }

        private void ApplyWindowMode(bool isMini)
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetWindowScale(hWnd);
            var presenter = (OverlappedPresenter)AppWindow.Presenter;

            var width  = isMini ? MiniWindowWidth  : FullWindowWidth;
            var height = isMini ? MiniWindowHeight : FullWindowHeight;

            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !isMini);
            presenter.IsResizable = !isMini;
            presenter.IsMaximizable = !isMini;
            AppTitleBar.Visibility = isMini ? Visibility.Collapsed : Visibility.Visible;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(width * scale),
                (int)Math.Round(height * scale)));
        }

        private void MiniExpandButton_Click(object sender, RoutedEventArgs e)
        {
            SetMiniMode(isMini: false);
        }

        private void MiniDragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsMiniMode) return;

            var currentPoint = e.GetCurrentPoint((UIElement)sender);
            if (!currentPoint.Properties.IsLeftButtonPressed) return;

            e.Handled = true;

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ReleaseCapture();
            SendMessage(hWnd, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
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
            _windowMessageMonitor.WindowMessageReceived -= OnWindowMessageReceived;
            _windowMessageMonitor.Dispose();
            AppWindow.IsShownInSwitchers = true;
        }

        private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
        {
            if (e.Message.MessageId == WmNclButtonDblClk && ViewModel.IsMiniMode)
            {
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId == WmQueryEndSession)
            {
                Debug.WriteLine("[Shutdown] WM_QUERYENDSESSION received");
                PrepareForSessionEnding();
                e.Result = new IntPtr(1);
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId != WmEndSession)
            {
                return;
            }

            if (e.Message.WParam == 0)
            {
                Debug.WriteLine("[Shutdown] WM_ENDSESSION cancelled");
                RestoreAfterSessionEndingCancelled();
                return;
            }

            Debug.WriteLine("[Shutdown] WM_ENDSESSION received");
            PrepareForSessionEnding();

            var cleanupTask = Task.Run(() =>
            {
                try
                {
                    if (Microsoft.UI.Xaml.Application.Current is App app)
                    {
                        app.HandleSessionEnding();
                    }
                    else
                    {
                        SystemProxyService.ClearProxy();
                        StopBackgroundServicesOnExit(fastShutdown: true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Shutdown] cleanup error: {ex}");
                }
            });
            cleanupTask.Wait(TimeSpan.FromMilliseconds(600));
            Environment.Exit(0);
        }

        private void PrepareForSessionEnding()
        {
            _isSessionEnding = true;
            _allowClose = true;
            _isHiddenToTray = false;
        }

        private void RestoreAfterSessionEndingCancelled()
        {
            _isSessionEnding = false;
            _allowClose = false;
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

        public void StopBackgroundServicesOnExit(bool fastShutdown = false)
        {
            ViewModel.ControlPanel.XrayService.StopForShutdown();
            ViewModel.ControlPanel.CleanupTunOnExit(fastShutdown);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process,
            IntPtr minimumWorkingSetSize,
            IntPtr maximumWorkingSetSize);
    }
}
