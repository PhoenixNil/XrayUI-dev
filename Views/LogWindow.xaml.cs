using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Models;
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
        private int _linesReceivedSinceFlush; // Background-thread increments; UI thread reads + clears.
        private int _prevBufferCount;

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

            this.SetWindowSize(900, 600);
            AppWindow.Title = L.Log_Title;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppTitleBar.Title = L.Log_Title;
            ThemeHelper.FollowAppTheme(this, WindowRoot);
            SystemBackdrop = new MicaBackdrop();

			ToolTipService.SetToolTip(LogPrivacyButton, L.Log_PrivacyTooltip);
            MaskAddressSubMenu.Text = L.Log_IpMask;
            MaskOffMenuItem.Text    = L.Log_MaskOff;
            LogLevelSubMenu.Text    = L.Log_Level;
            DnsLogSubMenu.Text      = L.Log_DnsLog;
            DnsLogOffMenuItem.Text  = L.Log_MaskOff;
            DnsLogOnMenuItem.Text   = L.Log_DnsLogOn;
            AutoScrollToggle.Content = L.Log_AutoScroll;
            CopyButton.Content       = L.Log_CopyAll;
            ClearButton.Content      = L.Log_Clear;

            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;

            RenderLog();
            UpdateStatus();
            _ = InitializeLogSettingsMenuAsync();

            _flushTimer = _queue.CreateTimer();
            _flushTimer.Interval = FlushInterval;
            _flushTimer.IsRepeating = true;
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            this.Closed += OnClosed;
        }

        // ── Event handlers ─────────────────────────────────────────────────────

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _flushTimer.Stop();
            _xray.LogReceived    -= OnLogReceived;
            _xray.RunningChanged -= OnRunningChanged;
        }

        private void OnLogReceived(object? sender, string line)
        {
            // Called from background thread. Do NOT touch the UI here —
            // just mark dirty; the timer will re-render on the UI thread.
            Interlocked.Increment(ref _linesReceivedSinceFlush);
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

            var autoScroll = AutoScrollToggle.IsChecked == true;
            var prevOffset = LogScrollViewer.VerticalOffset;
            var prevExtent = LogScrollViewer.ExtentHeight;
            var prevCount  = _prevBufferCount;
            var received   = Interlocked.Exchange(ref _linesReceivedSinceFlush, 0);

            RenderLog();
            var newCount = _prevBufferCount; // RenderLog just updated this.

            if (autoScroll)
            {
                LogScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
            }
            else
            {
                // Ring buffer evicted some lines: (received) − (net buffer growth) = lines pushed out.
                // Shift the scroll offset down by that height so visible content stays anchored.
                var evicted = Math.Max(0, received - (newCount - prevCount));
                if (evicted > 0 && prevCount > 0 && prevExtent > 0)
                {
                    var lineHeight = prevExtent / prevCount;
                    var target = Math.Max(0, prevOffset - evicted * lineHeight);
                    LogScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                }
            }
        }

        // ── Rendering ──────────────────────────────────────────────────────────

        private void RenderLog()
        {
            // XrayService owns the single source of truth; we just render a snapshot.
            var lines = _xray.GetLogBuffer();
            LogTextBlock.Text = string.Join('\n', lines);
            LineCountText.Text = Loc.Format("Log_Lines", lines.Count);
            _prevBufferCount = lines.Count;
        }

        private async Task InitializeLogSettingsMenuAsync()
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                SetMaskAddressSelection(LogMaskAddress.Normalize(settings.LogMaskAddress));
                SetLogLevelSelection(XrayLogLevel.Normalize(settings.XrayLogLevel));
                SetDnsLogSelection(settings.DnsLog);
            }
            catch
            {
                SetMaskAddressSelection(LogMaskAddress.Off);
                SetLogLevelSelection(XrayLogLevel.Warning);
                SetDnsLogSelection(false);
            }
        }

        private void SetMaskAddressSelection(string value)
        {
            MaskOffMenuItem.IsChecked     = value == LogMaskAddress.Off;
            MaskQuarterMenuItem.IsChecked = value == LogMaskAddress.Quarter;
            MaskHalfMenuItem.IsChecked    = value == LogMaskAddress.Half;
            MaskFullMenuItem.IsChecked    = value == LogMaskAddress.Full;
        }

        private void SetLogLevelSelection(string value)
        {
            LogLevelDebugMenuItem.IsChecked   = value == XrayLogLevel.Debug;
            LogLevelInfoMenuItem.IsChecked    = value == XrayLogLevel.Info;
            LogLevelWarningMenuItem.IsChecked = value == XrayLogLevel.Warning;
        }

        private void SetDnsLogSelection(bool value)
        {
            DnsLogOffMenuItem.IsChecked = !value;
            DnsLogOnMenuItem.IsChecked  = value;
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            StatusText.Text = running ? L.Log_Running : L.Log_NotRunning;
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
            Interlocked.Exchange(ref _linesReceivedSinceFlush, 0);
            RenderLog();
        }

        private async void MaskAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            // The off item carries the sentinel tag "off" (an empty-string Tag round-trips
            // unreliably through XAML); Normalize maps it back to Off ("").
            var value = LogMaskAddress.Normalize(item.Tag as string);
            SetMaskAddressSelection(value);

            await ApplyLogSettingAsync(L.Log_PrivacyTitle, s =>
            {
                if (LogMaskAddress.Normalize(s.LogMaskAddress) == value)
                {
                    return false;
                }

                s.LogMaskAddress = value;
                return true;
            });
        }

        private async void LogLevelMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = XrayLogLevel.Normalize(item.Tag as string);
            SetLogLevelSelection(value);

            await ApplyLogSettingAsync(L.Log_Level, s =>
            {
                if (XrayLogLevel.Normalize(s.XrayLogLevel) == value)
                {
                    return false;
                }

                s.XrayLogLevel = value;
                return true;
            });
        }

        private async void DnsLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = (item.Tag as string) == "on";
            SetDnsLogSelection(value);

            await ApplyLogSettingAsync(L.Log_DnsLog, s =>
            {
                if (s.DnsLog == value)
                {
                    return false;
                }

                s.DnsLog = value;
                return true;
            });
        }

        /// <summary>
        /// Shared save path for the log-settings flyout: persists the mutation, then hot-reapplies
        /// the config (proxy mode) or tells the user it applies next session (TUN mode).
        /// <paramref name="apply"/> returns false when the stored value already matches.
        /// </summary>
        private async Task ApplyLogSettingAsync(string title, Func<AppSettings, bool> apply)
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                if (!apply(settings))
                {
                    return;
                }

                await _settings.SaveSettingsAsync(settings);

                if (!_xray.IsRunning)
                {
                    return;
                }

                if (settings.IsTunMode)
                {
                    await ShowInfoAsync(title, L.Log_PrivacySaved);
                    return;
                }

                await _reapplyConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowInfoAsync(title, Loc.Format("Log_PrivacyFailed", ex.Message));
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
                CloseButtonText = L.Dialog_OK
            };

            await dialog.ShowAsync();
        }
    }
}
