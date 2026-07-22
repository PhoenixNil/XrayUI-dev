using System;
using System.ComponentModel;
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
        private readonly WindowMessageMonitor _windowMessageMonitor;
        // We own the tray icon directly (rather than WindowManager.IsVisibleInTray) so the
        // tooltip can track connection state; see ConfigureTray.
        private TrayIcon? _trayIcon;
        // Connection-state icon variants (idle = blue, running = green). Both the tray icon
        // and the taskbar/window icon swap between them; see ApplyConnectionIcon.
        private string? _idleIconPath;
        private string? _runningIconPath;
        // Last running-state we pushed to the icon, so we only issue a Shell_NotifyIcon icon
        // modify on an actual transition (see OnViewModelPropertyChanged for why that matters).
        private bool _trayShowsRunning;
        private bool _isSessionEnding;
        private bool _allowClose;
        private bool _initialized;
        private bool _isHiddenToTray;
        // Set when mini mode was entered from a maximized window, so expanding
        // returns to maximized instead of the plain windowed size.
        private bool _restoreMaximizedOnExpand;
        private bool _personalizeRealized;
        private readonly bool _startMinimized;
        // Set when we parked the window off-screen at startup; cleared after
        // we re-center it on the first user-initiated show (tray click).
        private bool _needsCenterOnFirstShow;

        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;
        private const uint WmHotkey = 0x0312;
        private const uint WmNclButtonDown   = 0x00A1;
        private const uint WmNclButtonDblClk = 0x00A3;
        private const int HtCaption = 0x0002;
        private const int FullWindowWidth = 950;
        private const int FullWindowHeight = 600;
        private const int FullModeMinWidth = 430;
        private const int FullModeMinHeight = 260;
        private const int MiniWindowWidth = 330;
        private const int MiniWindowHeight = 136;
        // Unique id for our own tray icon, independent of WinUIEx's WindowManager tray.
        // Must be a positive 16-bit value: the Shell_NotifyIcon v4 callback reports the icon id
        // in HIWORD(lParam) (a signed short), so a wider id is truncated on the way back and
        // every click would fail the id match in TrayIcon.ProcessTrayIconEvents (icon still
        // shows, but left/right clicks do nothing).
        private const uint TrayIconId = 0x5852;

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

            Title = L.MainWindow_Title;
            ToolTipService.SetToolTip(DockButton,        L.MainWindow_ToggleMini);
            ToolTipService.SetToolTip(MiniExpandButton,  L.MainWindow_ExpandFull);
            ToolTipService.SetToolTip(MinicloseButton,   L.MainWindow_Close);

            // Initial size and per-mode min-size constraints are established by
            // ApplyWindowMode(isMini: false) below. Get attaches WinUIEx window
            // management to this window for its side effects (min-size clamping,
            // placement); we don't keep the reference — the tray icon is owned
            // directly via _trayIcon so we can drive its tooltip from connection state.
            WindowManager.Get(this);
            _windowMessageMonitor = new WindowMessageMonitor(this);
            _windowMessageMonitor.WindowMessageReceived += OnWindowMessageReceived;
            GlobalHotkeyStore.HotkeysChanged += OnGlobalHotkeysChanged;

            _rootElement = (FrameworkElement)Content;
            ThemeHelper.RootElement = _rootElement;
            ThemeHelper.MainWindow = this;
            ThemeHelper.ApplyBackdrop("Mica");
            _rootElement.ActualThemeChanged += OnRootElementActualThemeChanged;

            // PersonalizeControl is heavy (5x ColorPicker, Expander, etc.). We
            // realize it lazily into PersonalizeHost on first show — saves the
            // entire subtree from cold-start construction. See OnViewModelPropertyChanged.
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            _miniDragRegion = (Border)_rootElement.FindName("MiniDragRegion");
            _miniExpandButton = (Button)_rootElement.FindName("MiniExpandButton");

            _miniDragRegion.PointerPressed += MiniDragRegion_PointerPressed;
            _miniExpandButton.Click += MiniExpandButton_Click;

            var miniCloseButton = (Button)_rootElement.FindName("MinicloseButton");
            miniCloseButton.Click += (_, _) =>
            {
                if (!HideToTray())
                    ExitApplication();
            };

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
            RegisterGlobalHotkeys();

            if (_startMinimized && !HideToTray())
            {
                RestoreFromTray();
            }
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
            var iconsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "icons");
            _idleIconPath = Path.Combine(iconsDir, "output.ico");
            var runningPath = Path.Combine(iconsDir, "running.ico");
            // Fall back to the idle icon if the running variant is missing, so a bad deploy
            // degrades to "icon never changes" rather than no icon at all.
            _runningIconPath = File.Exists(runningPath) ? runningPath : _idleIconPath;

            _trayShowsRunning = ViewModel.TrayShowsRunning;
            var iconPath = _trayShowsRunning ? _runningIconPath : _idleIconPath;
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
                _trayIcon = TryCreateTrayIcon(iconPath);
            }

            AppWindow.Closing += (_, args) =>
            {
                if (_allowClose || _isSessionEnding)
                {
                    return;
                }

                if (HideToTray())
                {
                    args.Cancel = true;
                }
            };
        }

        // Own the tray icon directly instead of WindowManager.IsVisibleInTray: WinUIEx ties that
        // built-in tooltip to the static window Title and never exposes it, so we create our own
        // TrayIcon and push the tooltip via Tooltip (NIM_MODIFY) — that updates szTip only,
        // leaving the taskbar/window icon untouched. Returns null if creation fails so the window
        // is never stranded in a tray that isn't there (HideToTray checks for it).
        private TrayIcon? TryCreateTrayIcon(string iconPath)
        {
            TrayIcon? trayIcon = null;
            try
            {
                trayIcon = new TrayIcon(TrayIconId, iconPath, ViewModel.TrayTooltip);
                trayIcon.Selected += (_, _) => RestoreFromTray();
                trayIcon.ContextMenu += (_, e) => e.Flyout = BuildTrayContextMenu();
                trayIcon.IsVisible = true;
                return trayIcon;
            }
            catch (Exception ex)
            {
                trayIcon?.Dispose();
                Debug.WriteLine($"[Tray] Failed to create tray icon: {ex.Message}");
                return null;
            }
        }

        // Swap both the tray icon and the taskbar/window icon to reflect connection state.
        // Called on the UI thread from the TrayTooltip PropertyChanged handler. SetIcon updates
        // the existing tray icon in place (NIM_MODIFY) — no dispose/recreate needed.
        private void ApplyConnectionIcon(bool running)
        {
            var path = running ? _runningIconPath : _idleIconPath;
            if (path is null || !File.Exists(path)) return;
            AppWindow.SetIcon(path);
            _trayIcon?.SetIcon(path);
        }

        private MenuFlyout BuildTrayContextMenu()
        {
            var flyout = new MenuFlyout();

            var openItem = new MenuFlyoutItem { Text = L.Tray_Open };
            openItem.Click += (_, _) => RestoreFromTray();
            flyout.Items.Add(openItem);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var exitItem = new MenuFlyoutItem { Text = L.Tray_Exit };
            exitItem.Click += (_, _) => ExitApplication();
            flyout.Items.Add(exitItem);

            return flyout;
        }

        private bool HideToTray()
        {
            if (_isHiddenToTray)
            {
                return true;
            }

            if (_trayIcon is null)
            {
                Debug.WriteLine("[Tray] Hide requested but tray icon is unavailable.");
                return false;
            }

            _isHiddenToTray = true;
            ControlPanel?.CloseLogWindow();
            ControlPanel?.CloseCustomRulesWindow();

            AppWindow.IsShownInSwitchers = false;
            AppWindow.Hide();

            // Collapse the root content so the compositor drops cached materials
            // for the visual tree while we're in the tray. Object graph stays
            // intact; restoring is one synchronous frame.
            _rootElement.Visibility = Visibility.Collapsed;

            ReleaseUiResources();
            return true;
        }

        internal void RestoreFromTray()
        {
            _isHiddenToTray = false;
            AppWindow.IsShownInSwitchers = true;
            _rootElement.Visibility = Visibility.Visible;

            if (_needsCenterOnFirstShow)
            {
                _needsCenterOnFirstShow = false;
                CenterOnPrimaryDisplay();
            }

            Activate();
        }

        private void HandleHotkeyMessage(int id)
        {
            if (id == GlobalHotkeyStore.ToggleId)
            {
                var cmd = ViewModel.ControlPanel.StartStopCommand;
                if (cmd.CanExecute(null))
                    _ = cmd.ExecuteAsync(null);
            }
            else if (id == GlobalHotkeyStore.RestoreId)
            {
                RestoreFromTray();
            }
        }

        private void OnGlobalHotkeysChanged(object? sender, EventArgs e) => RegisterGlobalHotkeys();

        /// <summary>
        /// Re-register after an elevation/process handoff. The outgoing process can still own
        /// the configured combinations when this window performs its normal startup registration;
        /// once App confirms that process has exited, retry on this window's UI thread.
        /// </summary>
        internal void RegisterGlobalHotkeysAfterProcessTakeover()
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                RegisterGlobalHotkeys();
                return;
            }

            if (!DispatcherQueue.TryEnqueue(RegisterGlobalHotkeys))
                Debug.WriteLine("[Hotkey] Failed to enqueue post-takeover hotkey registration.");
        }

        // Idempotent: always unregisters both ids first, then re-registers whichever have a
        // combo assigned (no separate enabled flag — presence of a combo means active). Safe to
        // call at startup and any time the Personalize page commits a hotkey change.
        private void RegisterGlobalHotkeys()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            foreach (var id in new[] { GlobalHotkeyStore.ToggleId, GlobalHotkeyStore.RestoreId })
            {
                HotkeyInterop.UnregisterHotKey(hWnd, id);

                // On failure, the combo is left in the store as-is (not cleared) — a conflict may
                // be temporary (another app releases it, or the system state changes), and the
                // next call to this method retries with the same combo. Nothing else reads a
                // "successfully registered" flag: the UI only reflects whether a combo is assigned,
                // and WM_HOTKEY simply never arrives for an id Windows didn't actually register.
                var (mods, vk) = GlobalHotkeyStore.GetCombo(id);
                if (vk != 0)
                    TryRegisterGlobalHotkey(hWnd, id, mods, vk);
            }
        }

        private static bool TryRegisterGlobalHotkey(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
        {
            if (HotkeyInterop.RegisterHotKey(hWnd, id, modifiers | GlobalHotkeyStore.ModNoRepeat, virtualKey))
                return true;

            Debug.WriteLine($"[Hotkey] Failed to register hotkey id={id} ({modifiers}:{virtualKey}). LastWin32Error={Marshal.GetLastWin32Error()}");
            return false;
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
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] Failed to remove tray icon during exit: {ex.Message}");
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);

                try
                {
                    if (Application.Current is App app)
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
            if (ViewModel.IsMiniMode == isMini) return;

            var incoming = isMini ? MiniModePanel : FullModePanel;

            // Snap to the new mode immediately so the click feels as instant as it
            // did before — no blocking fade-out. The only animation is a quick
            // entrance on the new content: stage it hidden so it doesn't flash when
            // its bound Visibility flips, switch modes + resize the HWND, then let
            // the new panel scale/fade in as a non-blocking flourish.
            WindowModeTransition.PrepareHidden(incoming);
            ViewModel.IsMiniMode = isMini;
            ApplyWindowMode(isMini);
            WindowModeTransition.FadeIn(incoming);
        }

        private void ApplyWindowMode(bool isMini)
        {
            var presenter = (OverlappedPresenter)AppWindow.Presenter;

            // Un-maximize first: resizing alone keeps the zoomed flag set, and
            // dragging a zoomed window restores it back to full-mode size.
            if (isMini)
            {
                _restoreMaximizedOnExpand = presenter.State == OverlappedPresenterState.Maximized;
                if (_restoreMaximizedOnExpand)
                    presenter.Restore();
            }

            var width  = isMini ? MiniWindowWidth  : FullWindowWidth;
            var height = isMini ? MiniWindowHeight : FullWindowHeight;

            // The WinUIEx min-size clamp (WM_GETMINMAXINFO) applies to SetWindowSize
            // too, so the full-mode minimum must be relaxed before shrinking to mini.
            var windowManager = WindowManager.Get(this);
            windowManager.MinWidth  = isMini ? MiniWindowWidth  : FullModeMinWidth;
            windowManager.MinHeight = isMini ? MiniWindowHeight : FullModeMinHeight;

            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !isMini);
            presenter.IsAlwaysOnTop = isMini;
            presenter.IsResizable = !isMini;
            presenter.IsMaximizable = !isMini;
            AppTitleBar.Visibility = isMini ? Visibility.Collapsed : Visibility.Visible;
            this.SetWindowSize(width, height);

            // Maximize only after the normal rect above is in place, so the restore
            // rect the system captures is the windowed size, not the mini rect.
            if (!isMini && _restoreMaximizedOnExpand)
            {
                _restoreMaximizedOnExpand = false;
                presenter.Maximize();
            }
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
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _rootElement.ActualThemeChanged -= OnRootElementActualThemeChanged;
            _windowMessageMonitor.WindowMessageReceived -= OnWindowMessageReceived;
            _windowMessageMonitor.Dispose();
            GlobalHotkeyStore.HotkeysChanged -= OnGlobalHotkeysChanged;
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            HotkeyInterop.UnregisterHotKey(hWnd, GlobalHotkeyStore.ToggleId);
            HotkeyInterop.UnregisterHotKey(hWnd, GlobalHotkeyStore.RestoreId);
            AppWindow.IsShownInSwitchers = true;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.TrayTooltip))
            {
                // Runs on the UI thread (VM raises PropertyChanged there); no dispatch needed.
                // Order matters: swap the icon FIRST, write the tooltip LAST. WinUIEx's SetIcon
                // issues a NIM_MODIFY carrying only the icon (no NIF_SHOWTIP); under
                // NOTIFYICON_VERSION_4 that suppresses the standard tooltip. The Tooltip setter
                // re-sends NIF_TIP|NIF_SHOWTIP, so it has to be the last modify we issue. We also
                // only swap on an actual running-state transition, so a tooltip-text change while
                // already running (e.g. a node switch) never drops the tooltip.
                var running = ViewModel.TrayShowsRunning;
                if (running != _trayShowsRunning)
                {
                    _trayShowsRunning = running;
                    ApplyConnectionIcon(running);
                }
                if (_trayIcon is not null)
                    _trayIcon.Tooltip = ViewModel.TrayTooltip;
                return;
            }

            if (_personalizeRealized) return;
            if (e.PropertyName != nameof(MainViewModel.PersonalizeVisibility)) return;
            if (ViewModel.PersonalizeVisibility != Visibility.Visible) return;

            _personalizeRealized = true;
            // Set ViewModel before adding to the visual tree so that, when the
            // host fires Loading, the UserControl's x:Bind initializers see a
            // non-null ViewModel and bind correctly the first time.
            PersonalizeHost.Children.Add(new Views.PersonalizeControl
            {
                ViewModel = ViewModel.Personalize,
            });
        }

        private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
        {
            if (e.Message.MessageId == WmNclButtonDblClk && ViewModel.IsMiniMode)
            {
                e.Handled = true;
                return;
            }

            if (e.Message.MessageId == WmHotkey)
            {
                HandleHotkeyMessage(unchecked((int)e.Message.WParam));
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
                    if (Application.Current is App app)
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
                // Compact LOH in this single GC pass — subscription/JSON paths
                // routinely allocate >85KB buffers that pin into LOH and never
                // compact under default settings.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
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
