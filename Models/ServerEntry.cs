using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    public partial class ServerEntry : ObservableObject
    {
        // Stable identifier for this entry; survives rename/dedupe.
        // Auto-generated for new entries and for legacy entries loaded from JSON without an ID.
        private string _id = System.Guid.NewGuid().ToString("N");

        public ServerEntry()
        {
            SubscriptionId = string.Empty;
            Name = string.Empty;
            Host = string.Empty;
            Protocol = string.Empty;
            Encryption = string.Empty;
            Username = string.Empty;
            Password = string.Empty;
            Uuid = string.Empty;
            Network = "tcp";
            Path = string.Empty;
            WsHost = string.Empty;
            Security = string.Empty;
            Sni = string.Empty;
            Fingerprint = string.Empty;
            EchConfigList = string.Empty;
            EchForceQuery = string.Empty;
            PublicKey = string.Empty;
            ShortId = string.Empty;
            SpiderX = string.Empty;
            Flow = string.Empty;
            VlessEncryption = string.Empty;
            Finalmask = string.Empty;
            ChainEntryServerId = string.Empty;
            ChainExitServerId = string.Empty;
            ChainEntryName = string.Empty;
            ChainExitName = string.Empty;
            ChainEntryProtocol = string.Empty;
            ChainExitProtocol = string.Empty;
        }

        /// <summary>ID of the subscription this node was imported from; empty = manually added.</summary>
        [ObservableProperty]
        public partial string SubscriptionId { get; set; }

        [ObservableProperty]
        public partial string Name { get; set; }

        [ObservableProperty]
        public partial string Host { get; set; }

        [ObservableProperty]
        public partial int Port { get; set; }

        /// <summary>ss | vmess | vless | hysteria2 | trojan | socks | chain</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayProtocol))]
        [NotifyPropertyChangedFor(nameof(IsChain))]
        [NotifyPropertyChangedFor(nameof(StandardListVisibility))]
        [NotifyPropertyChangedFor(nameof(ChainListVisibility))]
        public partial string Protocol { get; set; }

        /// <summary>Cipher for ss; "TLS" or "Reality" label for vless/vmess/hysteria2</summary>
        [ObservableProperty]
        public partial string Encryption { get; set; }

        [ObservableProperty]
        public partial bool IsActive { get; set; }

        [ObservableProperty]
        public partial bool IsFavorite { get; set; }

        // Auth
        [ObservableProperty]
        public partial string Username { get; set; }

        [ObservableProperty]
        public partial string Password { get; set; }

        [ObservableProperty]
        public partial string Uuid { get; set; }

        // Transport
        [ObservableProperty]
        public partial string Network { get; set; }

        [ObservableProperty]
        public partial string Path { get; set; }

        [ObservableProperty]
        public partial string WsHost { get; set; }

        [ObservableProperty]
        public partial int AlterId { get; set; }

        // TLS / Security
        [ObservableProperty]
        public partial string Security { get; set; }

        [ObservableProperty]
        public partial string Sni { get; set; }

        [ObservableProperty]
        public partial string Fingerprint { get; set; }

        [ObservableProperty]
        public partial bool AllowInsecure { get; set; }

        /// <summary>Client-side TLS ECH config list. Empty = disabled.</summary>
        [ObservableProperty]
        public partial string EchConfigList { get; set; }

        /// <summary>ECH force query mode: "half", "full", or empty/none.</summary>
        [ObservableProperty]
        public partial string EchForceQuery { get; set; }

        // VLESS Reality
        [ObservableProperty]
        public partial string PublicKey { get; set; }

        [ObservableProperty]
        public partial string ShortId { get; set; }

        [ObservableProperty]
        public partial string SpiderX { get; set; }

        /// <summary>VLESS flow: "xtls-rprx-vision" or empty string.</summary>
        [ObservableProperty]
        public partial string Flow { get; set; }

        /// <summary>VLESS user-level encryption (Xray PR #5067 PQ encryption, e.g. "mlkem768x25519plus...."). Empty = "none".</summary>
        [ObservableProperty]
        public partial string VlessEncryption { get; set; }

        /// <summary>Raw Xray streamSettings.finalmask JSON, shared as the "fm" URI parameter.</summary>
        [ObservableProperty]
        public partial string Finalmask { get; set; }

        // Proxy chain
        [ObservableProperty]
        public partial string ChainEntryServerId { get; set; }

        [ObservableProperty]
        public partial string ChainExitServerId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChainEntryDisplayName))]
        public partial string ChainEntryName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChainExitDisplayName))]
        public partial string ChainExitName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChainProtocolSummary))]
        public partial string ChainEntryProtocol { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChainProtocolSummary))]
        public partial string ChainExitProtocol { get; set; }

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? System.Guid.NewGuid().ToString("N") : value);
        }

        [JsonIgnore]
        public string DisplayProtocol => Protocol.ToLowerInvariant() switch
        {
            "ss" => "Shadowsocks",
            "vmess" => "VMess",
            "vless" => "VLESS",
            "hysteria2" => "Hysteria 2",
            "trojan" => "Trojan",
            "socks" => "SOCKS",
            "chain" => "ProxyChain",
            _ => Protocol ?? string.Empty
        };

        [JsonIgnore]
        public bool IsChain => string.Equals(Protocol, "chain", System.StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public Visibility StandardListVisibility => IsChain ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public Visibility ChainListVisibility => IsChain ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public string ChainEntryDisplayName => string.IsNullOrWhiteSpace(ChainEntryName)
            ? "(入口节点缺失)"
            : ChainEntryName;

        [JsonIgnore]
        public string ChainExitDisplayName => string.IsNullOrWhiteSpace(ChainExitName)
            ? "(出口节点缺失)"
            : ChainExitName;

        [JsonIgnore]
        public string ChainProtocolSummary
        {
            get
            {
                var entry = string.IsNullOrWhiteSpace(ChainEntryProtocol) ? "?" : ChainEntryProtocol;
                var exit = string.IsNullOrWhiteSpace(ChainExitProtocol) ? "?" : ChainExitProtocol;
                return $"{entry} -> {exit}";
            }
        }

        public void RefreshProtocolColor() => OnPropertyChanged(nameof(Protocol));
    }
}
