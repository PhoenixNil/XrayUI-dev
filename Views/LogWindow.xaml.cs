using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class LogWindow
    {
        // UI-update throttle: burst traffic (many lines/sec) collapses into
        // at most 1 re-render per interval instead of one per line.
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

        private static readonly SolidColorBrush RunningBrush =
            new(Windows.UI.Color.FromArgb(255, 34, 197, 94));   // green
        private static readonly SolidColorBrush StoppedBrush =
            new(Windows.UI.Color.FromArgb(255, 156, 163, 175)); // grey

        private readonly XrayService     _xray;
        private readonly SettingsService _settings;
        private readonly Func<Task> _reapplyConfigAsync;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _flushTimer;

        // Set from background thread when new lines arrive; consumed on UI thread.
        private volatile bool _dirty;

        public LogWindow(
            XrayService xray,
            SettingsService settings,
            Func<Task> reapplyConfigAsync)
        {
            this.InitializeComponent();
            _xray               = xray;
            _settings           = settings;
            _reapplyConfigAsync = reapplyConfigAsync;
            _queue              = DispatcherQueue.GetForCurrentThread();

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetWindowScale(hWnd);
            AppWindow.Resize(new SizeInt32((int)Math.Round(900 * scale), (int)Math.Round(600 * scale)));
            AppWindow.Title = "代理日志";

            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;

            RenderLog();
            UpdateStatus();
            _ = InitializeMaskAddressMenuAsync();

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

        private async Task InitializeMaskAddressMenuAsync()
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                SetMaskAddressSelection(LogMaskAddress.Normalize(settings.LogMaskAddress));
            }
            catch
            {
                SetMaskAddressSelection(LogMaskAddress.Off);
            }
        }

        private void SetMaskAddressSelection(string value)
        {
            MaskOffMenuItem.IsChecked     = value == LogMaskAddress.Off;
            MaskQuarterMenuItem.IsChecked = value == LogMaskAddress.Quarter;
            MaskHalfMenuItem.IsChecked    = value == LogMaskAddress.Half;
            MaskFullMenuItem.IsChecked    = value == LogMaskAddress.Full;
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            StatusText.Text = running ? "运行中" : "未运行";
            StatusDot.Fill  = running ? RunningBrush : StoppedBrush;
        }

        // ── Button handlers ────────────────────────────────────────────────────

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

        private async void MaskAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = LogMaskAddress.Normalize(item.Tag as string);
            SetMaskAddressSelection(value);

            try
            {
                var settings = await _settings.LoadSettingsAsync();
                if (LogMaskAddress.Normalize(settings.LogMaskAddress) == value)
                {
                    return;
                }

                settings.LogMaskAddress = value;
                await _settings.SaveSettingsAsync(settings);

                if (!_xray.IsRunning)
                {
                    return;
                }

                if (settings.IsTunMode)
                {
                    await ShowInfoAsync("日志隐私设置", "已保存，当前 TUN 会话下次启动时生效。");
                    return;
                }

                await _reapplyConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowInfoAsync("日志隐私设置", $"保存失败：{ex.Message}");
            }
        }

        private async Task ShowInfoAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ThemeHelper.ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = "确定"
            };

            await dialog.ShowAsync();
        }
    }
}
