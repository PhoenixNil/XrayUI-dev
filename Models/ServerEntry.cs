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
            Password = string.Empty;
            Uuid = string.Empty;
            Network = "tcp";
            Path = string.Empty;
            WsHost = string.Empty;
            Security = string.Empty;
            Sni = string.Empty;
            Fingerprint = string.Empty;
            PublicKey = string.Empty;
            ShortId = string.Empty;
            SpiderX = string.Empty;
            Flow = string.Empty;
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

        /// <summary>ss | vmess | vless | hysteria2 | trojan</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayProtocol))]
        public partial string Protocol { get; set; }

        /// <summary>Cipher for ss; "TLS" or "Reality" label for vless/vmess/hysteria2</summary>
        [ObservableProperty]
        public partial string Encryption { get; set; }

        [ObservableProperty]
        public partial bool IsActive { get; set; }

        // Auth
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
            _ => Protocol ?? string.Empty
        };

        public void RefreshProtocolColor() => OnPropertyChanged(nameof(Protocol));
    }
}
