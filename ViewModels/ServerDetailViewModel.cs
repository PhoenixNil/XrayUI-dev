using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class ServerDetailViewModel : ObservableObject
    {
        private static SolidColorBrush GetBrush(string key) =>
            (SolidColorBrush)Application.Current.Resources[key];

        private readonly LatencyProbeService _latencyProbe;
        private readonly AiUnlockCheckService _aiUnlockCheck;
        private CancellationTokenSource? _latencyTestCts;
        private CancellationTokenSource? _aiCheckCts;
        private int _latencyTestVersion;
        private ServerEntry? _activeServer;
        private bool _isProxyRunning;
        private AiUnlockStatus? _openAiStatus;
        private AiUnlockStatus? _claudeStatus;
        private AiUnlockStatus? _geminiStatus;
        private ServerEntry? _selectedServer;
        private string _latencyText = "Not tested";
        private bool _isTestingLatency;
        private SolidColorBrush _openAiStatusBrush = null!;
        private SolidColorBrush _claudeStatusBrush = null!;
        private SolidColorBrush _geminiStatusBrush = null!;

        public ServerDetailViewModel(LatencyProbeService latencyProbe, AiUnlockCheckService aiUnlockCheck)
        {
            _latencyProbe = latencyProbe;
            _aiUnlockCheck = aiUnlockCheck;
            ResetAiUnlockDisplay();
        }

        public ServerEntry? SelectedServer
        {
            get => _selectedServer;
            set
            {
                var oldValue = _selectedServer;
                if (SetProperty(ref _selectedServer, value))
                {
                    OnSelectedServerChanged(oldValue, value);
                }
            }
        }

        public ServerEntry? ActiveServer
        {
            get => _activeServer;
            set
            {
                if (ReferenceEquals(_activeServer, value))
                {
                    return;
                }

                _activeServer = value;
                UpdateAiUnlockDisplay();
            }
        }

        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            private set
            {
                if (_isProxyRunning == value)
                {
                    return;
                }

                _isProxyRunning = value;
                UpdateAiUnlockDisplay();
            }
        }

        public string SelectedName => SelectedServer?.Name ?? "未选择服务器";

        public string SelectedHost => SelectedServer?.Host ?? "-";

        public string SelectedPort => SelectedServer?.Port.ToString() ?? "-";

        public string SelectedProtocol => SelectedServer?.DisplayProtocol ?? "-";

        public string SelectedSecurityLabel
            => string.Equals(SelectedServer?.Protocol, "ss", StringComparison.OrdinalIgnoreCase)
                ? "加密"
                : "安全";

        public string SelectedEncryption => SelectedServer?.Encryption ?? "-";

        public string SelectedShareLink
            => SelectedServer is null ? string.Empty : (NodeLinkSerializer.ToLink(SelectedServer) ?? string.Empty);

        public string SelectedTransport
        {
            get
            {
                if (SelectedServer is null)
                {
                    return "TCP";
                }

                if (string.Equals(SelectedServer.Protocol, "hysteria2", StringComparison.OrdinalIgnoreCase))
                {
                    return "UDP";
                }

                return (SelectedServer.Network?.ToLowerInvariant()) switch
                {
                    "ws" => "WebSocket",
                    "grpc" => "gRPC",
                    "xhttp" => "XHTTP",
                    _ => "TCP"
                };
            }
        }

        public string LatencyText
        {
            get => _latencyText;
            set => SetProperty(ref _latencyText, value);
        }

        public bool IsTestingLatency
        {
            get => _isTestingLatency;
            set
            {
                if (SetProperty(ref _isTestingLatency, value))
                {
                    OnIsTestingLatencyChanged(value);
                }
            }
        }

        public bool CanTestLatency => !IsTestingLatency && SelectedServer is not null;

        // ── AI Unlock indicators ──────────────────────────────────────────────

        public SolidColorBrush OpenAiStatusBrush
        {
            get => _openAiStatusBrush;
            set => SetProperty(ref _openAiStatusBrush, value);
        }

        public SolidColorBrush ClaudeStatusBrush
        {
            get => _claudeStatusBrush;
            set => SetProperty(ref _claudeStatusBrush, value);
        }

        public SolidColorBrush GeminiStatusBrush
        {
            get => _geminiStatusBrush;
            set => SetProperty(ref _geminiStatusBrush, value);
        }

        private void OnSelectedServerChanged(ServerEntry? oldValue, ServerEntry? newValue)
        {
            if (oldValue is not null)
            {
                oldValue.PropertyChanged -= OnSelectedServerPropertyChanged;
            }

            if (newValue is not null)
            {
                newValue.PropertyChanged += OnSelectedServerPropertyChanged;
            }

            CancelPendingLatencyTest();
            NotifySelectedServerFieldsChanged();
            UpdateAiUnlockDisplay();

            if (newValue is null)
            {
                ResetLatencyDisplay();
                return;
            }

            _ = TestLatency();
        }

        private void OnIsTestingLatencyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanTestLatency));
            TestLatencyCommand.NotifyCanExecuteChanged();
        }

        private void OnSelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ServerEntry.Name):
                    OnPropertyChanged(nameof(SelectedName));
                    break;
                case nameof(ServerEntry.Host):
                    OnPropertyChanged(nameof(SelectedHost));
                    CancelPendingLatencyTest();
                    ResetLatencyDisplay();
                    break;
                case nameof(ServerEntry.Port):
                    OnPropertyChanged(nameof(SelectedPort));
                    CancelPendingLatencyTest();
                    ResetLatencyDisplay();
                    break;
                case nameof(ServerEntry.Protocol):
                    OnPropertyChanged(nameof(SelectedProtocol));
                    OnPropertyChanged(nameof(SelectedSecurityLabel));
                    OnPropertyChanged(nameof(SelectedTransport));
                    break;
                case nameof(ServerEntry.Encryption):
                    OnPropertyChanged(nameof(SelectedEncryption));
                    break;
                case nameof(ServerEntry.Network):
                    OnPropertyChanged(nameof(SelectedTransport));
                    break;
                case null:
                case "":
                    CancelPendingLatencyTest();
                    NotifySelectedServerFieldsChanged();
                    ResetLatencyDisplay();
                    break;
            }
        }

        private void NotifySelectedServerFieldsChanged()
        {
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(SelectedPort));
            OnPropertyChanged(nameof(SelectedProtocol));
            OnPropertyChanged(nameof(SelectedSecurityLabel));
            OnPropertyChanged(nameof(SelectedEncryption));
            OnPropertyChanged(nameof(SelectedTransport));
            OnPropertyChanged(nameof(SelectedShareLink));
            OnPropertyChanged(nameof(CanTestLatency));
            TestLatencyCommand.NotifyCanExecuteChanged();
        }

        private void ResetLatencyDisplay()
        {
            LatencyText = "未测试";
            
        }

        private void ResetAiUnlockDisplay()
        {
            var neutral = GetBrush("StateNeutralBrush");
            OpenAiStatusBrush = neutral;
            ClaudeStatusBrush = neutral;
            GeminiStatusBrush = neutral;
        }

        private void ClearAiUnlockResults()
        {
            _openAiStatus = null;
            _claudeStatus = null;
            _geminiStatus = null;
        }

        private void UpdateAiUnlockDisplay()
        {
            if (!IsProxyRunning || SelectedServer is null || !ReferenceEquals(SelectedServer, ActiveServer))
            {
                ResetAiUnlockDisplay();
                return;
            }

            OpenAiStatusBrush = ResolveAiUnlockBrush(_openAiStatus);
            ClaudeStatusBrush = ResolveAiUnlockBrush(_claudeStatus);
            GeminiStatusBrush = ResolveAiUnlockBrush(_geminiStatus);
        }

        private static SolidColorBrush ResolveAiUnlockBrush(AiUnlockStatus? status) => status switch
        {
            AiUnlockStatus.Unlocked => GetBrush("StateSuccessBrush"),
            AiUnlockStatus.Blocked  => GetBrush("StateErrorBrush"),
            _                       => GetBrush("StateNeutralBrush")
        };

        private void CancelPendingLatencyTest()
        {
            _latencyTestVersion++;
            var cts = _latencyTestCts;
            _latencyTestCts = null;
            IsTestingLatency = false;

            if (cts is not null)
            {
                _ = CancelLatencyTestAsync(cts);
            }
        }

        private static Task CancelLatencyTestAsync(CancellationTokenSource cts)
        {
            return Task.Run(() =>
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception)
                {
                }
            });
        }

        private void CancelPendingAiCheck()
        {
            _aiCheckCts?.Cancel();
            _aiCheckCts = null;
        }

        /// <summary>
        /// Called by the view / MainViewModel when the proxy starts or stops.
        /// </summary>
        public void OnProxyRunningChanged(bool isRunning, int httpProxyPort)
        {
            CancelPendingAiCheck();
            IsProxyRunning = isRunning;

            if (!isRunning)
            {
                ClearAiUnlockResults();
                UpdateAiUnlockDisplay();
                return;
            }

            ClearAiUnlockResults();
            UpdateAiUnlockDisplay();
            _ = RunAiUnlockChecksAsync(httpProxyPort);
        }

        private async Task RunAiUnlockChecksAsync(int httpProxyPort)
        {
            var cts = new CancellationTokenSource();
            _aiCheckCts = cts;

            try
            {
                // Run all checks in parallel
                var openAiTask = _aiUnlockCheck.CheckOpenAiAsync(httpProxyPort, cts.Token);
                var claudeTask = _aiUnlockCheck.CheckClaudeAsync(httpProxyPort, cts.Token);
                var geminiTask = _aiUnlockCheck.CheckGeminiAsync(httpProxyPort, cts.Token);

                var results = await Task.WhenAll(openAiTask, claudeTask, geminiTask);

                if (cts.IsCancellationRequested) return;

                _openAiStatus = results[0];
                _claudeStatus = results[1];
                _geminiStatus = results[2];
                UpdateAiUnlockDisplay();
            }
            catch (OperationCanceledException)
            {
                // cancelled — leave as-is
            }
            finally
            {
                cts.Dispose();
                if (ReferenceEquals(_aiCheckCts, cts))
                    _aiCheckCts = null;
            }
        }

        [RelayCommand(CanExecute = nameof(CanTestLatency))]
        private async Task TestLatency()
        {
            var server = SelectedServer;
            if (server is null)
            {
                return;
            }

            var version = _latencyTestVersion;
            var cts = new CancellationTokenSource();
            _latencyTestCts = cts;

            IsTestingLatency = true;
            LatencyText = "测试中...";

            try
            {
                var token = cts.Token;
                var result = await Task.Run(
                    () => _latencyProbe.ProbeAsync(server, TimeSpan.FromSeconds(3), token),
                    token);

                if (!IsCurrentLatencyTest(version, cts, server))
                {
                    return;
                }

                LatencyText = result.Status switch
                {
                    LatencyProbeStatus.Success => $"{result.Milliseconds ?? 0} ms",
                    LatencyProbeStatus.Timeout => "超时",
                    _ => "失败"
                };
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            finally
            {
                var isCurrentTest = IsCurrentLatencyTest(version, cts, server);

                cts.Dispose();

                if (ReferenceEquals(_latencyTestCts, cts))
                {
                    _latencyTestCts = null;
                }

                if (isCurrentTest)
                {
                    IsTestingLatency = false;
                }
            }
        }

        private bool IsCurrentLatencyTest(
            int version,
            CancellationTokenSource cts,
            ServerEntry server)
        {
            return version == _latencyTestVersion
                   && !cts.IsCancellationRequested
                   && ReferenceEquals(SelectedServer, server);
        }
    }
}


