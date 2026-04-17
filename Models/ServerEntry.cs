using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    public class ServerEntry : INotifyPropertyChanged
    {
        private string _name        = string.Empty;
        private string _host        = string.Empty;
        private int    _port;
        private string _protocol    = string.Empty;
        private string _encryption  = string.Empty;
        private bool   _isActive;

        // ── Auth ──────────────────────────────────────────────────────────────
        private string _password    = string.Empty;
        private string _uuid        = string.Empty;

        // ── Transport ─────────────────────────────────────────────────────────
        private string _network     = "tcp";   // tcp | ws | grpc | udp
        private string _path        = string.Empty;
        private string _wsHost      = string.Empty;
        private int    _alterId;

        // ── TLS / Security ────────────────────────────────────────────────────
        private string _security    = string.Empty;  // none | tls | reality
        private string _sni         = string.Empty;
        private string _fingerprint = string.Empty;
        private bool   _allowInsecure;

        // ── VLESS Reality ─────────────────────────────────────────────────────
        private string _publicKey   = string.Empty;
        private string _shortId     = string.Empty;
        private string _spiderX     = string.Empty;
        private string _flow        = string.Empty;

        // ── Base ──────────────────────────────────────────────────────────────
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Host
        {
            get => _host;
            set { _host = value; OnPropertyChanged(); }
        }

        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        /// <summary>ss | vmess | vless | hysteria2</summary>
        public string Protocol
        {
            get => _protocol;
            set
            {
                _protocol = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayProtocol));
            }
        }

        [JsonIgnore]
        public string DisplayProtocol => (Protocol).ToLowerInvariant() switch
        {
            "ss" => "Shadowsocks",
            "vmess" => "VMess",
            "vless" => "VLESS",
            "hysteria2" => "Hysteria 2",
            _ => Protocol ?? string.Empty
        };

        /// <summary>Cipher for ss; "TLS" or "Reality" label for vless/vmess/hysteria2</summary>
        public string Encryption
        {
            get => _encryption;
            set { _encryption = value; OnPropertyChanged(); }
        }

        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }

        // ── Auth ──────────────────────────────────────────────────────────────
        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        public string Uuid
        {
            get => _uuid;
            set { _uuid = value; OnPropertyChanged(); }
        }

        // ── Transport ─────────────────────────────────────────────────────────
        public string Network
        {
            get => _network;
            set { _network = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public string WsHost
        {
            get => _wsHost;
            set { _wsHost = value; OnPropertyChanged(); }
        }

        public int AlterId
        {
            get => _alterId;
            set { _alterId = value; OnPropertyChanged(); }
        }

        // ── TLS / Security ────────────────────────────────────────────────────
        public string Security
        {
            get => _security;
            set { _security = value; OnPropertyChanged(); }
        }

        public string Sni
        {
            get => _sni;
            set { _sni = value; OnPropertyChanged(); }
        }

        public string Fingerprint
        {
            get => _fingerprint;
            set { _fingerprint = value; OnPropertyChanged(); }
        }

        public bool AllowInsecure
        {
            get => _allowInsecure;
            set { _allowInsecure = value; OnPropertyChanged(); }
        }

        // ── VLESS Reality ─────────────────────────────────────────────────────
        public string PublicKey
        {
            get => _publicKey;
            set { _publicKey = value; OnPropertyChanged(); }
        }

        public string ShortId
        {
            get => _shortId;
            set { _shortId = value; OnPropertyChanged(); }
        }

        public string SpiderX
        {
            get => _spiderX;
            set { _spiderX = value; OnPropertyChanged(); }
        }

        /// <summary>VLESS flow: "xtls-rprx-vision" 或空字符串（无 flow）</summary>
        public string Flow
        {
            get => _flow;
            set { _flow = value; OnPropertyChanged(); }
        }

        // ── Color refresh ─────────────────────────────────────────────────────

        public void RefreshProtocolColor() => OnPropertyChanged(nameof(Protocol));

        // ── INotifyPropertyChanged ────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
