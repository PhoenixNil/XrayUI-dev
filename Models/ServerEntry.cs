using System.Text.Json.Serialization;
using XrayUI.Helpers;

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
            XhttpMode = string.Empty;
            XhttpExtra = string.Empty;
            Security = string.Empty;
            Sni = string.Empty;
            Fingerprint = string.Empty;
            PinnedPeerCertSha256 = string.Empty;
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
            WgPrivateKey = string.Empty;
            WgPublicKey = string.Empty;
            WgPreSharedKey = string.Empty;
            WgLocalAddress = string.Empty;
            WgReserved = string.Empty;
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

        /// <summary>ss | vmess | vless | hysteria2 | trojan | anytls | socks | http | wireguard | chain</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayProtocol))]
        [NotifyPropertyChangedFor(nameof(IsChain))]
        public partial string Protocol { get; set; }

        /// <summary>Cipher for ss; "TLS" / "Reality" / "None" label for vless/vmess/trojan/anytls/hysteria2; "WireGuard" for wireguard</summary>
        [ObservableProperty]
        public partial string Encryption { get; set; }

        // Runtime-only flag (which server xray is currently proxying through).
        // Never persisted — otherwise an export captures it and a later import
        // would resurrect the badge on a stale server.
        [JsonIgnore]
        [ObservableProperty]
        public partial bool IsActive { get; set; }

        // Runtime-only latency probe result in milliseconds; null = not yet measured.
        // Never persisted — a probe is meaningless across sessions and would bloat exports.
        [JsonIgnore]
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LatencyText))]
        [NotifyPropertyChangedFor(nameof(HasLatency))]
        public partial int? LatencyMs { get; set; }

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

        /// <summary>XHTTP mode: auto | packet-up | stream-up | stream-one. Empty = xray default (auto).</summary>
        [ObservableProperty]
        public partial string XhttpMode { get; set; }

        /// <summary>Raw xhttpSettings.extra JSON (padding/xmux settings), shared as the "extra" URI parameter.</summary>
        [ObservableProperty]
        public partial string XhttpExtra { get; set; }

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

        /// <summary>
        /// Verify the server certificate against the Windows certificate store instead of the
        /// CA list bundled in the core (tlsSettings.useSystemCA). Off by default, matching the
        /// core; only a node signed by a private CA installed on this machine needs it. Applies
        /// wherever a tlsSettings block is emitted, so not to REALITY. No share-link convention
        /// carries it, so it is set by hand in the edit dialog.
        /// </summary>
        [ObservableProperty]
        public partial bool UseSystemCa { get; set; }

        /// <summary>
        /// Server certificate fingerprint (SHA-256 hex) to pin instead of validating the chain.
        /// Maps to tlsSettings.pinnedPeerCertSha256, so it applies to every TLS outbound — but
        /// not to REALITY, which authenticates by public key and has no tlsSettings.
        /// Carried in hysteria2 share links as "pinSHA256" and in anytls links as "hpkp" (the key
        /// mihomo reads); other protocols have no share-link convention for a pin here, so their
        /// entries get one from a Clash config's `fingerprint` or by hand in the edit dialog.
        /// </summary>
        [ObservableProperty]
        public partial string PinnedPeerCertSha256 { get; set; }

        /// <summary>Client-side TLS ECH config list. Empty = disabled.</summary>
        [ObservableProperty]
        public partial string EchConfigList { get; set; }

        /// <summary>
        /// ECH force query mode: "half", "full", or empty/none. The core removed the option
        /// (ECH is forced whenever a config list is set), so this is no longer written into the
        /// config or editable; it survives only so imported share links round-trip unchanged.
        /// </summary>
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

        // WireGuard
        /// <summary>WireGuard client secret (private) key, base64. Maps to xray settings.secretKey.</summary>
        [ObservableProperty]
        public partial string WgPrivateKey { get; set; }

        /// <summary>WireGuard peer public key, base64. Maps to xray peers[0].publicKey.</summary>
        [ObservableProperty]
        public partial string WgPublicKey { get; set; }

        /// <summary>Optional WireGuard pre-shared key, base64. Maps to xray peers[0].preSharedKey.</summary>
        [ObservableProperty]
        public partial string WgPreSharedKey { get; set; }

        /// <summary>Comma-separated local tunnel CIDRs (e.g. "172.16.0.2/32,fd00::2/128"). Maps to xray settings.address.</summary>
        [ObservableProperty]
        public partial string WgLocalAddress { get; set; }

        /// <summary>Optional WireGuard reserved bytes as "a,b,c". Maps to xray settings.reserved when it yields 3 ints.</summary>
        [ObservableProperty]
        public partial string WgReserved { get; set; }

        /// <summary>WireGuard tunnel MTU. 0 = let the config builder use its default.</summary>
        [ObservableProperty]
        public partial int WgMtu { get; set; }

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? System.Guid.NewGuid().ToString("N") : value);
        }

        [JsonIgnore]
        public string DisplayProtocol => GetDisplayProtocol(Protocol);

        /// <summary>Maps a protocol code (as stored on <see cref="Protocol"/>) to its display name.</summary>
        public static string GetDisplayProtocol(string? protocol) => protocol?.ToLowerInvariant() switch
        {
            "ss" => "Shadowsocks",
            "vmess" => "VMess",
            "vless" => "VLESS",
            "hysteria2" => "Hysteria 2",
            "trojan" => "Trojan",
            "anytls" => "AnyTLS",
            "socks" => "SOCKS",
            "http" => "HTTP",
            "wireguard" => "WireGuard",
            "chain" => "ProxyChain",
            _ => protocol ?? string.Empty
        };

        [JsonIgnore]
        public bool IsChain => string.Equals(Protocol, "chain", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Latency formatted for display: "30 ms" for a measurement, a timeout label for a
        /// failed probe (negative sentinel such as -1), empty when not measured.
        /// </summary>
        [JsonIgnore]
        public string LatencyText => LatencyMs switch
        {
            null   => string.Empty,
            < 0    => L.ServerDetail_Timeout,
            int ms => Loc.Format("ServerDetail_LatencyMs", ms),
        };

        /// <summary>Whether a measured latency is available to show in the list row.</summary>
        [JsonIgnore]
        public bool HasLatency => LatencyMs.HasValue;

        public void RefreshProtocolColor() => OnPropertyChanged(nameof(Protocol));

        /// <summary>
        /// Copies every persisted connection-config field (including display name) from
        /// <paramref name="source"/>, leaving identity and runtime state (Id, SubscriptionId,
        /// IsFavorite, IsActive, LatencyMs) untouched. Used when a subscription refresh keeps
        /// the live instance of the connected node: identity-key fields are equal by
        /// construction, but fields outside the key (fingerprint, ECH, finalmask, WireGuard
        /// keys, ...) may have changed and must not be silently dropped.
        /// When adding a new persisted config property, add it here as well.
        /// </summary>
        public void CopyConfigFrom(ServerEntry source)
        {
            Name               = source.Name;
            Host               = source.Host;
            Port               = source.Port;
            Protocol           = source.Protocol;
            Encryption         = source.Encryption;
            Username           = source.Username;
            Password           = source.Password;
            Uuid               = source.Uuid;
            Network            = source.Network;
            Path               = source.Path;
            WsHost             = source.WsHost;
            XhttpMode          = source.XhttpMode;
            XhttpExtra         = source.XhttpExtra;
            AlterId            = source.AlterId;
            Security           = source.Security;
            Sni                = source.Sni;
            Fingerprint        = source.Fingerprint;
            AllowInsecure      = source.AllowInsecure;
            UseSystemCa        = source.UseSystemCa;
            PinnedPeerCertSha256 = source.PinnedPeerCertSha256;
            EchConfigList      = source.EchConfigList;
            EchForceQuery      = source.EchForceQuery;
            PublicKey          = source.PublicKey;
            ShortId            = source.ShortId;
            SpiderX            = source.SpiderX;
            Flow               = source.Flow;
            VlessEncryption    = source.VlessEncryption;
            Finalmask          = source.Finalmask;
            ChainEntryServerId = source.ChainEntryServerId;
            ChainExitServerId  = source.ChainExitServerId;
            WgPrivateKey       = source.WgPrivateKey;
            WgPublicKey        = source.WgPublicKey;
            WgPreSharedKey     = source.WgPreSharedKey;
            WgLocalAddress     = source.WgLocalAddress;
            WgReserved         = source.WgReserved;
            WgMtu              = source.WgMtu;
        }
    }
}
