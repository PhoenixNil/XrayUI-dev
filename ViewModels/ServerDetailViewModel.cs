using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using XrayUI.Helpers;
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
        private AiUnlockStatus? _openAiStatus;
        private AiUnlockStatus? _claudeStatus;
        private AiUnlockStatus? _geminiStatus;

        public ServerDetailViewModel(LatencyProbeService latencyProbe, AiUnlockCheckService aiUnlockCheck)
        {
            _latencyProbe = latencyProbe;
            _aiUnlockCheck = aiUnlockCheck;
            LatencyText = L.ServerDetail_NotTested;
            ShowLatencyInDetails = true;
            ShowAiUnlockInDetails = true;
            ShowGroupInDetails = true;
            ResetAiUnlockDisplay();
        }

        public Func<IEnumerable<ServerEntry>> GetAllServers { get; set; } = () => Array.Empty<ServerEntry>();

        /// <summary>
        /// Resolves a node's group label. Set by MainViewModel so this VM can read the
        /// subscription list that lives in ServerListViewModel without a back-reference.
        /// </summary>
        public Func<ServerEntry?, string> ResolveGroupName { get; set; } = _ => string.Empty;

        /// <summary>
        /// The running core's local socks/http port (ControlPanelViewModel.ActiveLocalProxyPort),
        /// set by MainViewModel. Read at the moment the AI checks start, never cached: a reapply
        /// can move the port without the running state changing, and a check deferred while the
        /// AI row was hidden would otherwise dial the dead one and paint every service blocked.
        /// </summary>
        public Func<int?> GetActiveLocalProxyPort { get; set; } = () => null;

        private ServerEntry? ResolveChainServer(string id)
            => string.IsNullOrEmpty(id) ? null : GetAllServers().FirstOrDefault(s => s.Id == id);

        [ObservableProperty]
        public partial ServerEntry? SelectedServer { get; set; }

        [ObservableProperty]
        public partial ServerEntry? ActiveServer { get; set; }

        [ObservableProperty]
        public partial bool IsProxyRunning { get; private set; }

        public string SelectedName => SelectedServer?.Name ?? L.ServerDetail_NoServer;

        public string SelectedHostLabel
            => SelectedServer?.IsChain == true ? L.ServerDetail_Entry : L.ServerDetail_Address;

        public string SelectedHost
            => SelectedServer?.IsChain == true
                ? ResolveChainServer(SelectedServer.ChainEntryServerId)?.Name ?? L.ServerDetail_EntryMissing
                : SelectedServer?.Host ?? "-";

        public string SelectedPortLabel
            => SelectedServer?.IsChain == true ? L.ServerDetail_Exit : L.ServerDetail_Port;

        public string SelectedPort
            => SelectedServer?.IsChain == true
                ? ResolveChainServer(SelectedServer.ChainExitServerId)?.Name ?? L.ServerDetail_ExitMissing
                : SelectedServer?.Port.ToString() ?? "-";

        public string SelectedProtocol => SelectedServer?.DisplayProtocol ?? "-";

        public string SelectedGroupName => ResolveGroupName(SelectedServer);

        /// <summary>
        /// Opens subscription management. Set by MainViewModel — same indirection as
        /// <see cref="GetAllServers"/>, so this VM never references ServerListViewModel.
        /// </summary>
        public Func<Task> OpenSubscriptions { get; set; } = () => Task.CompletedTask;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(GroupVisibility))]
        public partial bool ShowGroupInDetails { get; set; }

        // Only for nodes that actually came from a subscription — the value is a link into
        // subscription management, and a manually added node has nothing on the other end of it.
        // Expressed via CanOpenGroup so the row and the link can never disagree.
        public Visibility GroupVisibility
            => ShowGroupInDetails && CanOpenGroup ? Visibility.Visible : Visibility.Collapsed;

        public bool CanOpenGroup => !string.IsNullOrEmpty(SelectedServer?.SubscriptionId);

        // Favourite marks the node, not its group, so the star trails the card title. It is an
        // inline Run rather than a sibling element: a separate FontIcon would need either a fixed
        // width on the name (killing its wrap) or right-alignment (detaching the star from the
        // text it marks). Empty string when not favourited — the Run then occupies nothing.
        // Segoe Fluent Icons FavoriteStarFill, with the separator space baked in so the two
        // Runs need no significant whitespace between their XAML tags.
        private const string FavoriteStarFill = " \uE735";

        public string FavoriteGlyph => SelectedServer?.IsFavorite == true ? FavoriteStarFill : string.Empty;

        /// <summary>
        /// Title text for screen readers. The star is a private-use codepoint that reads as garbage,
        /// so the favourite state has to be spelled out here instead of inferred from the glyph.
        /// </summary>
        public string SelectedNameAutomationText
            => SelectedServer?.IsFavorite == true
                ? $"{SelectedName} {L.ServerDetail_Favorited}"
                : SelectedName;

        [RelayCommand(CanExecute = nameof(CanOpenGroup))]
        private Task OpenGroup() => OpenSubscriptions();

        /// <summary>
        /// Re-reads the group label. Needed for edits that change the label without touching the
        /// selected <see cref="ServerEntry"/> itself — renaming or deleting a subscription.
        /// </summary>
        public void RefreshGroupName()
        {
            OnPropertyChanged(nameof(SelectedGroupName));
            OnPropertyChanged(nameof(GroupVisibility));
            OnPropertyChanged(nameof(CanOpenGroup));
            OpenGroupCommand.NotifyCanExecuteChanged();
        }

        public string SelectedSecurityLabel
            => (SelectedServer?.Protocol?.ToLowerInvariant()) switch
            {
                "ss"    => L.ServerDetail_Encryption,
                "socks" => L.ServerDetail_AuthLabel,
                "http"  => L.ServerDetail_AuthLabel,
                "chain" => L.ServerDetail_ChainLabel,
                _       => L.ServerDetail_Security
            };

        public string SelectedEncryption
        {
            get
            {
                if (SelectedServer is null) return "-";
                switch (SelectedServer.Protocol?.ToLowerInvariant())
                {
                    case "socks":
                    case "http":
                        return string.IsNullOrWhiteSpace(SelectedServer.Username)
                               && string.IsNullOrWhiteSpace(SelectedServer.Password)
                            ? L.ServerDetail_NoAuth
                            : L.ServerDetail_UserPass;
                    case "chain":
                        var entry = ResolveChainServer(SelectedServer.ChainEntryServerId)?.DisplayProtocol ?? "?";
                        var exit = ResolveChainServer(SelectedServer.ChainExitServerId)?.DisplayProtocol ?? "?";
                        return $"{entry} -> {exit}";
                    default:
                        return SelectedServer.Encryption ?? "-";
                }
            }
        }

        public string SelectedShareLink
            => SelectedServer is null ? string.Empty : (NodeLinkSerializer.ToLink(SelectedServer) ?? string.Empty);

        public string SelectedTransport
        {
            get
            {
                if (SelectedServer is null)
                {
                    return "-";
                }

                if (string.Equals(SelectedServer.Protocol, "hysteria2", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(SelectedServer.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase))
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

        public Visibility SelectedTransportVisibility
            => SelectedServer?.IsChain == true ? Visibility.Collapsed : Visibility.Visible;

        [ObservableProperty]
        public partial string LatencyText { get; set; }

        [ObservableProperty]
        public partial bool IsTestingLatency { get; set; }

        public bool CanTestLatency => !IsTestingLatency && SelectedServer is not null;

        public bool CanCopyShareLink => !string.IsNullOrWhiteSpace(SelectedShareLink);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LatencyVisibility))]
        public partial bool ShowLatencyInDetails { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AiUnlockVisibility))]
        public partial bool ShowAiUnlockInDetails { get; set; }

        public Visibility LatencyVisibility => ShowLatencyInDetails ? Visibility.Visible : Visibility.Collapsed;

        public Visibility AiUnlockVisibility => ShowAiUnlockInDetails ? Visibility.Visible : Visibility.Collapsed;

        partial void OnShowAiUnlockInDetailsChanged(bool value)
        {
            if (value)
                StartAiUnlockChecksIfNeeded();
        }

        // ── AI Unlock indicators ──────────────────────────────────────────────

        [ObservableProperty]
        public partial SolidColorBrush OpenAiStatusBrush { get; set; }

        [ObservableProperty]
        public partial SolidColorBrush ClaudeStatusBrush { get; set; }

        [ObservableProperty]
        public partial SolidColorBrush GeminiStatusBrush { get; set; }

        partial void OnSelectedServerChanged(ServerEntry? oldValue, ServerEntry? newValue)
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

        partial void OnActiveServerChanged(ServerEntry? value) => UpdateAiUnlockDisplay();

        partial void OnIsProxyRunningChanged(bool value) => UpdateAiUnlockDisplay();

        partial void OnIsTestingLatencyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanTestLatency));
            TestLatencyCommand.NotifyCanExecuteChanged();
        }

        private void OnSelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName))
            {
                CancelPendingLatencyTest();
                NotifySelectedServerFieldsChanged();
                ResetLatencyDisplay();
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(ServerEntry.Name):
                    OnPropertyChanged(nameof(SelectedName));
                    OnPropertyChanged(nameof(SelectedNameAutomationText));
                    break;
                case nameof(ServerEntry.Host):
                    OnPropertyChanged(nameof(SelectedHostLabel));
                    OnPropertyChanged(nameof(SelectedHost));
                    CancelPendingLatencyTest();
                    ResetLatencyDisplay();
                    break;
                case nameof(ServerEntry.Port):
                    OnPropertyChanged(nameof(SelectedPortLabel));
                    OnPropertyChanged(nameof(SelectedPort));
                    CancelPendingLatencyTest();
                    ResetLatencyDisplay();
                    break;
                case nameof(ServerEntry.Protocol):
                    OnPropertyChanged(nameof(SelectedProtocol));
                    OnPropertyChanged(nameof(SelectedHostLabel));
                    OnPropertyChanged(nameof(SelectedHost));
                    OnPropertyChanged(nameof(SelectedPortLabel));
                    OnPropertyChanged(nameof(SelectedPort));
                    OnPropertyChanged(nameof(SelectedSecurityLabel));
                    OnPropertyChanged(nameof(SelectedTransport));
                    OnPropertyChanged(nameof(SelectedTransportVisibility));
                    break;
                case nameof(ServerEntry.Encryption):
                case nameof(ServerEntry.Username):
                case nameof(ServerEntry.Password):
                    OnPropertyChanged(nameof(SelectedEncryption));
                    break;
                case nameof(ServerEntry.ChainEntryServerId):
                    OnPropertyChanged(nameof(SelectedHost));
                    OnPropertyChanged(nameof(SelectedEncryption));
                    // A chain's latency is measured at its entry hop.
                    CancelPendingLatencyTest();
                    ResetLatencyDisplay();
                    break;
                case nameof(ServerEntry.ChainExitServerId):
                    OnPropertyChanged(nameof(SelectedPort));
                    OnPropertyChanged(nameof(SelectedEncryption));
                    break;
                case nameof(ServerEntry.Network):
                    OnPropertyChanged(nameof(SelectedTransport));
                    break;
                case nameof(ServerEntry.SubscriptionId):
                    RefreshGroupName();
                    break;
                case nameof(ServerEntry.IsFavorite):
                    OnPropertyChanged(nameof(FavoriteGlyph));
                    OnPropertyChanged(nameof(SelectedNameAutomationText));
                    break;
            }

            // Any persisted config field can feed NodeLinkSerializer, so refresh the share link
            // for everything except runtime/display-only notifications — a field added to
            // ServerEntry later then can't silently leave the copied link stale.
            if (e.PropertyName is not (nameof(ServerEntry.IsActive)
                or nameof(ServerEntry.LatencyMs)
                or nameof(ServerEntry.LatencyText)
                or nameof(ServerEntry.HasLatency)
                or nameof(ServerEntry.IsTestingLatency)
                or nameof(ServerEntry.DisplayProtocol)
                or nameof(ServerEntry.IsChain)))
            {
                OnPropertyChanged(nameof(SelectedShareLink));
                OnPropertyChanged(nameof(CanCopyShareLink));
            }
        }

        private void NotifySelectedServerFieldsChanged()
        {
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedHostLabel));
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(SelectedPortLabel));
            OnPropertyChanged(nameof(SelectedPort));
            OnPropertyChanged(nameof(SelectedProtocol));
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(SelectedNameAutomationText));
            RefreshGroupName();
            OnPropertyChanged(nameof(SelectedSecurityLabel));
            OnPropertyChanged(nameof(SelectedEncryption));
            OnPropertyChanged(nameof(SelectedTransport));
            OnPropertyChanged(nameof(SelectedTransportVisibility));
            OnPropertyChanged(nameof(SelectedShareLink));
            OnPropertyChanged(nameof(CanCopyShareLink));
            OnPropertyChanged(nameof(CanTestLatency));
            TestLatencyCommand.NotifyCanExecuteChanged();
        }

        private void ResetLatencyDisplay()
        {
            LatencyText = L.ServerDetail_NotTested;
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
        public void OnProxyRunningChanged(bool isRunning)
        {
            CancelPendingAiCheck();
            IsProxyRunning = isRunning;

            ClearAiUnlockResults();
            UpdateAiUnlockDisplay();
            StartAiUnlockChecksIfNeeded();
        }

        /// <summary>
        /// Runs the three checks for the live session unless their results could not be seen:
        /// while the AI row is hidden they are three HTTPS round trips through the new node on
        /// every connect, competing with the user's own first requests. Showing the row
        /// mid-session re-enters here; a session that already has results or an in-flight check
        /// is left alone.
        /// </summary>
        private void StartAiUnlockChecksIfNeeded()
        {
            if (!IsProxyRunning || !ShowAiUnlockInDetails || _aiCheckCts is not null)
                return;

            if (_openAiStatus is not null || _claudeStatus is not null || _geminiStatus is not null)
                return;

            // No local port means a config profile publishes no socks/http inbound, so there is
            // nothing to probe through. Leave the dots neutral instead of running the checks:
            // every failure path in AiUnlockCheckService reports Blocked, which would paint a
            // red "this node is blocked" for what is really "we never reached the core".
            if (GetActiveLocalProxyPort() is { } port)
                _ = RunAiUnlockChecksAsync(port);
        }

        private async Task RunAiUnlockChecksAsync(int httpProxyPort)
        {
            var cts = new CancellationTokenSource();
            _aiCheckCts = cts;

            // Update each indicator the moment its own check returns, instead of
            // batching with Task.WhenAll (which would gate every dot on the slowest
            // check — Gemini). The await resumes on the UI thread, so touching the
            // status brushes here is safe.
            async Task RunOne(Task<AiUnlockStatus> check, Action<AiUnlockStatus> assign)
            {
                var status = await check;
                if (cts.IsCancellationRequested) return;
                assign(status);
                UpdateAiUnlockDisplay();
            }

            try
            {
                // All three requests are kicked off before the first await, so they
                // still run in parallel.
                await Task.WhenAll(
                    RunOne(_aiUnlockCheck.CheckOpenAiAsync(httpProxyPort, cts.Token), s => _openAiStatus = s),
                    RunOne(_aiUnlockCheck.CheckClaudeAsync(httpProxyPort, cts.Token), s => _claudeStatus = s),
                    RunOne(_aiUnlockCheck.CheckGeminiAsync(httpProxyPort, cts.Token), s => _geminiStatus = s));
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
            LatencyText = L.ServerDetail_Testing;

            try
            {
                var token = cts.Token;
                // Snapshot on the UI thread: the probe reads it off-thread to resolve a chain's
                // current entry hop, and the live collection is only safe to read here.
                var allServers = GetAllServers().ToArray();
                var result = await Task.Run(
                    () => _latencyProbe.ProbeAsync(server, allServers, TimeSpan.FromSeconds(3), token),
                    token);

                if (!IsCurrentLatencyTest(version, cts, server))
                {
                    return;
                }

                LatencyText = result.Status switch
                {
                    LatencyProbeStatus.Success => Loc.Format("ServerDetail_LatencyMs", result.Milliseconds ?? 0),
                    LatencyProbeStatus.Timeout => L.ServerDetail_Timeout,
                    _                          => L.ServerDetail_Failed
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


