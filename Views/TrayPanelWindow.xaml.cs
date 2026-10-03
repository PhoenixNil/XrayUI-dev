using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinUIEx;
using XrayUI.Helpers;

namespace XrayUI.Views
{
    /// <summary>
    /// Host for the tray panel: a chrome-free, always-on-top window that rests at the bottom-right
    /// of the primary work area and slides in from behind the taskbar, the way the Windows quick
    /// settings do. Created once and kept; the panel inside is rebuilt on every show and dropped on
    /// hide, so a hidden panel holds no view-model subscriptions.
    /// </summary>
    public sealed partial class TrayPanelWindow
    {
        // Gap to the work-area edges, the same the Windows flyouts keep.
        private const double EdgeMarginDip = 12;
        private const double SlideInMs = 260;
        private const double SlideOutMs = 200;
        // The header icon's size in TrayPanelControl.
        private const double AppIconDip = 20;

        private readonly IntPtr _hwnd;
        private readonly ActiveAcrylicBackdrop _backdrop = new();
        private readonly string _idleIconPath;
        private readonly string _runningIconPath;
        private ImageSource? _idleIcon;
        private ImageSource? _runningIcon;
        private bool _iconsRequested;
        private TrayPanelViewModel? _viewModel;
        // Light dismiss only counts a deactivation that follows an activation of this show: a
        // window that never got the foreground would otherwise close the instant it opened.
        private bool _activatedSinceShow;
        // Just below the display's bottom edge, where the panel waits off-screen.
        private int _parkedY;
        // The running slide's per-frame step; see StartSlide.
        private EventHandler<object>? _slideFrame;

        /// <param name="idleIconPath">The tray icon shown while disconnected; the panel's header
        /// shows the same picture.</param>
        /// <param name="runningIconPath">The tray icon shown while connected.</param>
        public TrayPanelWindow(string idleIconPath, string runningIconPath)
        {
            _idleIconPath = idleIconPath;
            _runningIconPath = runningIconPath;
            InitializeComponent();
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsResizable   = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Closing += OnClosing;

            var cornerPreference = DwmwcpRound;
            _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

            ThemeHelper.FollowAppTheme(this, WindowRoot);
            // After FollowAppTheme, so the backdrop starts from the seeded theme.
            SystemBackdrop = _backdrop;

            Activated += OnActivated;
        }

        /// <summary>Set by <see cref="ShowPanel"/> and cleared by <see cref="HidePanel"/>, so it
        /// is already false while the panel slides away.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Tick count when the panel last started hiding.</summary>
        public long LastHiddenAt { get; private set; }

        /// <summary>Raised once the panel has slid away and released its content.</summary>
        public event EventHandler? Hidden;

        /// <summary>Raised when losing activation is about to close the panel.</summary>
        public event EventHandler? LightDismissed;

        /// <summary>Shows a fresh panel bound to <paramref name="viewModel"/>, which this window
        /// then owns and disposes when the panel hides.</summary>
        public void ShowPanel(TrayPanelViewModel viewModel)
        {
            if (IsOpen)
            {
                viewModel.Dispose();
                return;
            }

            StopSlide();
            ReleasePanel();

            _viewModel = viewModel;
            var panel = new TrayPanelControl(viewModel);
            panel.SetAppIcons(_idleIcon, _runningIcon);
            panel.Loaded += OnPanelLoaded;
            WindowRoot.Children.Add(panel);

            IsOpen = true;
            _activatedSinceShow = false;
            _backdrop.SyncTheme(WindowRoot.ActualTheme);

            // Wait just below the display until the panel has measured itself; the slide starts
            // from here.
            var bounds = DisplayArea.Primary.OuterBounds;
            _parkedY = bounds.Y + bounds.Height;
            AppWindow.Move(new PointInt32(bounds.X + bounds.Width - AppWindow.Size.Width, _parkedY));

            Activate();
            _ = this.SetForegroundWindow();
            PlaceBelowTaskbar();
        }

        public void HidePanel()
        {
            if (!IsOpen) return;

            IsOpen = false;
            LastHiddenAt = Environment.TickCount64;
            PlaceBelowTaskbar();
            StartSlide(_parkedY, SlideOutMs, decelerates: false, OnHideCompleted);
        }

        private void OnPanelLoaded(object sender, RoutedEventArgs e)
        {
            var panel = (TrayPanelControl)sender;
            panel.Loaded -= OnPanelLoaded;
            if (!IsOpen || !ReferenceEquals(panel.ViewModel, _viewModel)) return;

            // Unconstrained, so the panel reports its natural size rather than the window's.
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var scale = WindowRoot.XamlRoot.RasterizationScale;
            LoadAppIcons(scale);
            // Not ResizeClient: it sizes for a caption this window doesn't have and leaves the
            // panel a caption's height too tall. The frame measured off the live window is exact.
            var outer = AppWindow.Size;
            var client = AppWindow.ClientSize;
            AppWindow.Resize(new SizeInt32(
                ToPixels(panel.DesiredSize.Width, scale) + outer.Width - client.Width,
                ToPixels(panel.DesiredSize.Height, scale) + outer.Height - client.Height));

            // The window rect includes an invisible resize frame; it is the visible edge that keeps
            // the margin from the work area.
            var (visibleRight, visibleBottom) = GetVisibleExtent();
            var work = DisplayArea.Primary.WorkArea;
            var margin = ToPixels(EdgeMarginDip, scale);
            AppWindow.Move(new PointInt32(work.X + work.Width - margin - visibleRight, _parkedY));

            var restingY = work.Y + work.Height - margin - visibleBottom;
            StartSlide(restingY, SlideInMs, decelerates: true, OnShowCompleted);
            panel.FocusFirstControl();
        }

        private void OnShowCompleted()
        {
            // Clear of the taskbar now, so step back up to the top of the topmost band: tucked under
            // the taskbar it would also sit under the notification area's overflow, which stays
            // open when the panel is opened from an icon in there.
            _ = SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        /// <summary>Offsets of the visible frame's right and bottom edges from the window's
        /// top-left corner; the full window size when DWM can't say.</summary>
        private (int Right, int Bottom) GetVisibleExtent()
        {
            var size = AppWindow.Size;
            if (!GetWindowRect(_hwnd, out var window)
                || DwmGetWindowAttribute(_hwnd, DwmwaExtendedFrameBounds, out var visible, Marshal.SizeOf<NativeRect>()) != 0)
            {
                return (size.Width, size.Height);
            }

            return (visible.Right - window.Left, visible.Bottom - window.Top);
        }

        private void OnHideCompleted()
        {
            AppWindow.Hide();
            ReleasePanel();
            Hidden?.Invoke(this, EventArgs.Empty);
        }

        private void ReleasePanel()
        {
            WindowRoot.Children.Clear();
            _viewModel?.Dispose();
            _viewModel = null;
        }

        // ── App icon ──────────────────────────────────────────────────────────

        // Once per window, on the first show: the frame to decode depends on the DPI, which is
        // only known once the window is on screen.
        private async void LoadAppIcons(double scale)
        {
            if (_iconsRequested) return;
            _iconsRequested = true;

            var pixels = ToPixels(AppIconDip, scale);
            // Both started before either is awaited, so the two decodes overlap.
            var idle = LoadIconFrameAsync(_idleIconPath, pixels);
            var running = LoadIconFrameAsync(_runningIconPath, pixels);
            _idleIcon = await idle;
            _runningIcon = await running;

            if (WindowRoot.Children.Count > 0 && WindowRoot.Children[0] is TrayPanelControl panel)
                panel.SetAppIcons(_idleIcon, _runningIcon);
        }

        // XAML decodes an .ico through its first frame, which in both tray icons is 16×16 and
        // blurs at the header's size; this picks the smallest frame that covers it instead.
        private static async Task<ImageSource?> LoadIconFrameAsync(string path, int wantedPixels)
        {
            try
            {
                using var file = File.OpenRead(path);
                using var stream = file.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);

                BitmapFrame? pick = null;
                for (uint i = 0; i < decoder.FrameCount; i++)
                {
                    var frame = await decoder.GetFrameAsync(i);
                    if (pick is null || IsBetterFrame(frame.PixelWidth, pick.PixelWidth, wantedPixels))
                        pick = frame;
                }
                if (pick is null) return null;

                var bitmap = await pick.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                return source;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrayPanel] Failed to load {path}: {ex.Message}");
                return null;
            }
        }

        // The smallest frame at least the wanted width; failing that, the largest there is.
        private static bool IsBetterFrame(uint candidate, uint current, int wanted) =>
            candidate >= wanted
                ? current < wanted || candidate < current
                : current < wanted && candidate > current;

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _activatedSinceShow = true;
                return;
            }

            // Light dismiss, as the Windows flyouts do: a click anywhere else closes the panel.
            if (_activatedSinceShow && IsOpen)
            {
                LightDismissed?.Invoke(this, EventArgs.Empty);
                HidePanel();
            }
        }

        // Alt+F4 hides rather than closes: the window is kept for the next show.
        private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            HidePanel();
        }

        private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            HidePanel();
        }

        // ── Slide ─────────────────────────────────────────────────────────────
        // The window itself has to move — the backdrop belongs to the HWND, so sliding only the
        // content would leave the acrylic behind. Frames are driven by the XAML render loop.

        private void StartSlide(int toY, double durationMs, bool decelerates, Action completed)
        {
            StopSlide();
            var from = AppWindow.Position;
            var clock = Stopwatch.StartNew();
            _slideFrame = (_, _) =>
            {
                var t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / durationMs);
                // Cubic ease-out to arrive, ease-in to leave: the Fluent entrance and exit curves.
                var eased = decelerates ? 1 - Math.Pow(1 - t, 3) : t * t * t;
                AppWindow.Move(new PointInt32(from.X, (int)Math.Round(from.Y + (toY - from.Y) * eased)));
                if (t < 1.0) return;

                StopSlide();
                completed();
            };
            CompositionTarget.Rendering += _slideFrame;
        }

        private void StopSlide()
        {
            if (_slideFrame is null) return;
            CompositionTarget.Rendering -= _slideFrame;
            _slideFrame = null;
        }

        // Tucks the window directly under the taskbar, so it slides out from behind the taskbar
        // instead of across it. Activation raises the window above everything, so this has to
        // follow it.
        private void PlaceBelowTaskbar()
        {
            var taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;
            _ = SetWindowPos(_hwnd, taskbar, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }

        private static int ToPixels(double dip, double scale) => (int)Math.Ceiling(dip * scale);

        private const int DwmwaExtendedFrameBounds = 9;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTopmost = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr FindWindowW(string lpClassName, string? lpWindowName);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    }
}
