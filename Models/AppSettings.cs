using System.Collections.Generic;
using System.Text.Json.Nodes;
using XrayUI.Services;

namespace XrayUI.Models
{
    public class AppSettings
    {
        public int LocalMixedPort { get; set; } = 16890;
        /// <summary>When true, the local socks/http inbound listens on 0.0.0.0 instead of
        /// 127.0.0.1 so other devices on the LAN can use this machine as a proxy.</summary>
        public bool AllowLanConnections { get; set; } = false;
        /// <summary>"smart" | "global"</summary>
        public string RoutingMode { get; set; } = "smart";
        /// <summary>Domestic region treated as direct in smart routing: "cn" | "ru" | "ir".
        /// Maps to geosite/geoip tokens in <see cref="XrayUI.Services.XrayConfigBuilder"/>.</summary>
        public string RoutingRegion { get; set; } = "cn";
        /// <summary>Whether TUN mode is enabled.</summary>
        public bool IsTunMode { get; set; } = false;
        public string? LastTunServerHost { get; set; }
        public int TunMtu { get; set; } = XrayConfigConstants.TunMtuDefault;
        public string TunOutboundInterface { get; set; } = XrayConfigConstants.TunOutboundInterfaceAuto;
        /// <summary>Route IPv6 (::/0) through the TUN adapter too. Opt-in; off by default to avoid
        /// IPv6 leak / Happy-Eyeballs stalls on IPv4-only networks.</summary>
        public bool TunIpv6Enabled { get; set; } = false;
        public bool IsStartupEnabled { get; set; } = false;
        public bool IsAutoConnect    { get; set; } = false;
        /// <summary>true = global proxy (default); false = do not take over the system proxy.</summary>
        public bool IsSystemProxyEnabled { get; set; } = true;
        /// <summary>Stable ID (ServerEntry.Id) of the most recently connected server — used for auto-connect on boot.</summary>
        public string? LastAutoConnectServerId { get; set; }
        /// <summary>Legacy (pre-Id) name-based setting. Read once for migration on first load after upgrade.</summary>
        public string? LastAutoConnectServerName { get; set; }
        /// <summary>Stable ID (ServerEntry.Id) of the server selected in the list — restored on next launch
        /// (including the TUN elevated relaunch, which is a brand-new process).</summary>
        public string? LastSelectedServerId { get; set; }
        /// <summary>"" | "quarter" | "half" | "full"; controls Xray log IP masking.</summary>
        public string LogMaskAddress { get; set; } = string.Empty;
        /// <summary>Legacy verbose toggle (true → "info", false → "warning"). SettingsService
        /// migrates this into <see cref="XrayLogLevel"/> on load; not read anywhere else.</summary>
        public bool VerboseXrayLog { get; set; } = false;
        /// <summary>"debug" | "info" | "warning". Populated from the legacy
        /// <see cref="VerboseXrayLog"/> bool by SettingsService.LoadSettingsAsync if unset, so
        /// other readers can treat null as "not yet migrated" rather than re-deriving the
        /// fallback themselves. See <see cref="XrayUI.Services.XrayLogLevel"/>. Independent of
        /// the access log, which always prints one [inbound -> outbound] verdict line per
        /// connection.</summary>
        public string? XrayLogLevel { get; set; }
        /// <summary>Enable Xray's per-query DNS resolution log (log.dnsLog).</summary>
        public bool DnsLog { get; set; } = false;

        // ── Internationalization ──────────────────────────────────────────────
        /// <summary>BCP-47 tag from <see cref="XrayUI.Helpers.LanguageHelper.SupportedLanguages"/>, or null to follow system.</summary>
        public string? Language { get; set; }

        // ── Personalization ───────────────────────────────────────────────────
        /// <summary>"Light" | "Dark" | "Default" (follows system)</summary>
        public string? ThemeSetting { get; set; }
        /// <summary>"Mica" | "Acrylic"</summary>
        public string? BackdropSetting { get; set; }
        public string? ColorSs        { get; set; }
        public string? ColorVless     { get; set; }
        public string? ColorVmess     { get; set; }
        public string? ColorHysteria2 { get; set; }
        public string? ColorFallback  { get; set; }
        public bool ShowLatencyInDetails { get; set; } = true;
        public bool ShowAiUnlockInDetails { get; set; } = true;
        /// <summary>Expand the server-list filter bar when the app starts.</summary>
        public bool OpenServerFilterPanelOnStartup { get; set; } = false;

        // ── Global hotkeys ────────────────────────────────────────────────────
        // No separate enabled flag — a hotkey is active whenever a combo is assigned, matching
        // PowerToys' shortcut behavior. Assign via the recorder button, clear via its right-click menu.
        /// <summary>"mods:vk" — Win32 RegisterHotKey MOD_* flags and virtual-key code, or null if never set.</summary>
        public string? HotkeyToggleCombo { get; set; }
        /// <summary>"mods:vk", or null if never set.</summary>
        public string? HotkeyRestoreCombo { get; set; }

        // ── DNS ───────────────────────────────────────────────────────────────
        /// <summary>Direct DNS for domestic domains (geosite:cn). null = choose the default based on TUN mode.</summary>
        public string? DirectDnsServer { get; set; }
        /// <summary>Proxy DNS for foreign domains, resolved through the proxy outbound. null = use the default 8.8.8.8.</summary>
        public string? ProxyDnsServer { get; set; }
        /// <summary>Values from <see cref="XrayUI.Services.DnsQueryStrategy"/>.</summary>
        public string DnsQueryStrategy { get; set; } = "UseIPv4";
        public bool DnsCacheEnabled { get; set; } = true;
        /// <summary>Enable xray FakeDNS. Only takes effect when IsTunMode is true.</summary>
        public bool FakeDnsEnabled { get; set; } = false;

        // ── Custom routing rules ──────────────────────────────────────────────
        /// <summary>User-defined routing rules. Applied only when RoutingMode == "smart".</summary>
        public List<CustomRoutingRule>? CustomRules { get; set; }

        /// <summary>
        /// Advanced routing JSON: a complete xray routing object ({ domainStrategy, balancers?, rules }).
        /// When non-null, replaces the default routing template; required TUN rules are still
        /// inserted at the start of rules, and CustomRules are still appended to the end.
        /// Only active when RoutingMode == "smart".
        /// </summary>
        public JsonObject? AdvancedRouting { get; set; }

        // ── Subscriptions ─────────────────────────────────────────────────────
        /// <summary>Persisted subscription sources. Nodes derived from these carry SubscriptionId = the entry's Id.</summary>
        public List<SubscriptionEntry>? Subscriptions { get; set; }
    }
}
