using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class LogWindow
    {
        // UI-update throttle: burst traffic (many lines/sec) collapses into
        // at most 1 re-render per interval instead of one per line.
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

        private readonly XrayService     _xray;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _flushTimer;

        // Set from background thread when new lines arrive; consumed on UI thread.
        private volatile bool _dirty;

        public LogWindow(XrayService xray)
        {
            this.InitializeComponent();
            _xray  = xray;
            _queue = DispatcherQueue.GetForCurrentThread();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetWindowScale(hWnd);
            AppWindow.Resize(new SizeInt32((int)Math.Round(900 * scale), (int)Math.Round(600 * scale)));
            AppWindow.Title = "代理日志";

            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;

            // Initial render of any already-buffered lines.
            RenderLog();
            UpdateStatus();

            _flushTimer = _queue.CreateTimer();
            _flushTimer.Interval = FlushInterval;
            _flushTimer.IsRepeating = true;
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            this.Closed += (_, _) =>
            {
                _flushTimer.Stop();
                _xray.LogReceived    -= OnLogReceived;
                _xray.RunningChanged -= OnRunningChanged;
            };
        }

        // ── Event handlers ─────────────────────────────────────────────────────

        private void OnLogReceived(object? sender, string line)
        {
            // Called from background thread. Do NOT touch the UI here —
            // just mark dirty; the timer will re-render on the UI thread.
            _dirty = true;
        }

        private void OnRunningChanged(object? sender, bool running)
        {
            _queue.TryEnqueue(UpdateStatus);
        }

        private void OnFlushTick(DispatcherQueueTimer sender, object args)
        {
            if (!_dirty) return;
            _dirty = false;

            RenderLog();

            if (AutoScrollToggle.IsChecked == true)
            {
                LogScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
            }
        }

        // ── Rendering ──────────────────────────────────────────────────────────

        private void RenderLog()
        {
            // XrayService owns the single source of truth; we just render a snapshot.
            var lines = _xray.GetLogBuffer();
            LogTextBlock.Text = string.Join('\n', lines);
            LineCountText.Text = $"({lines.Count} 行)";
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            StatusText.Text = running ? "运行中" : "未运行";
            StatusDot.Fill  = new SolidColorBrush(
                running
                    ? Windows.UI.Color.FromArgb(255, 34, 197, 94)    // green
                    : Windows.UI.Color.FromArgb(255, 156, 163, 175)); // grey
        }

        // ── Button handlers ────────────────────────────────────────────────────

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

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var dp = new DataPackage();
            dp.SetText(LogTextBlock.Text);
            Clipboard.SetContent(dp);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _xray.ClearLogBuffer();
            RenderLog();
        }
    }
}
