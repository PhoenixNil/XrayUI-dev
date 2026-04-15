using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class LogWindow
    {
        private const int MaxLines = 2000;

        private readonly XrayService    _xray;
        private readonly List<string>   _lines = new();
        private readonly DispatcherQueue _queue;

        public LogWindow(XrayService xray)
        {
            this.InitializeComponent();
            _xray  = xray;
            _queue = DispatcherQueue.GetForCurrentThread();

            AppWindow.Resize(new SizeInt32(900, 600));
            AppWindow.Title = "代理日志";

            // Load existing log buffer from service
            foreach (var line in xray.GetLogBuffer())
                _lines.Add(line);

            FlushToTextBlock();
            UpdateStatus();

            // Subscribe to new log lines
            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;

            this.Closed += (_, _) =>
            {
                _xray.LogReceived    -= OnLogReceived;
                _xray.RunningChanged -= OnRunningChanged;
            };
        }

        // ── Event handlers ─────────────────────────────────────────────────────

        private void OnLogReceived(object? sender, string line)
        {
            // Called from background thread — marshal to UI
            _queue.TryEnqueue(() =>
            {
                AddLine(line);
                if (AutoScrollToggle.IsChecked == true)
                    LogScrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
            });
        }

        private void OnRunningChanged(object? sender, bool running)
        {
            _queue.TryEnqueue(UpdateStatus);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void AddLine(string line)
        {
            _lines.Add(line);

            // Trim excess lines
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);

            // Append to TextBlock directly (faster than full rebuild)
            LogTextBlock.Text = LogTextBlock.Text.Length == 0
                ? line
                : LogTextBlock.Text + "\n" + line;

            UpdateLineCount();
        }

        private void FlushToTextBlock()
        {
            LogTextBlock.Text = string.Join("\n", _lines);
            UpdateLineCount();
        }

        private void UpdateLineCount()
        {
            LineCountText.Text = $"({_lines.Count} 行)";
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            StatusText.Text = running ? "运行中" : "未运行";
            StatusDot.Fill   = new SolidColorBrush(
                running
                    ? Windows.UI.Color.FromArgb(255, 34, 197, 94)   // green
                    : Windows.UI.Color.FromArgb(255, 156, 163, 175)); // grey
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
            _lines.Clear();
            _xray.ClearLogBuffer();
            LogTextBlock.Text = string.Empty;
            UpdateLineCount();
        }
    }
}
