using System.Collections.Generic;

namespace XrayUI.Models
{
    public class AppSettings
    {
        public int LocalMixedPort { get; set; } = 16890;
        /// <summary>"smart" | "global"</summary>
        public string RoutingMode { get; set; } = "smart";
        /// <summary>TUN 模式是否已启用</summary>
        public bool IsTunMode { get; set; } = false;
        public string? LastTunServerHost { get; set; }
        public bool IsStartupEnabled { get; set; } = false;
        public bool IsAutoConnect    { get; set; } = false;
        /// <summary>true = 全局代理 (default); false = 不接管代理</summary>
        public bool IsSystemProxyEnabled { get; set; } = true;
        /// <summary>Stable ID (ServerEntry.Id) of the most recently connected server — used for auto-connect on boot.</summary>
        public string? LastAutoConnectServerId { get; set; }
        /// <summary>Legacy (pre-Id) name-based setting. Read once for migration on first load after upgrade.</summary>
        public string? LastAutoConnectServerName { get; set; }
        /// <summary>"" | "quarter" | "half" | "full"; controls Xray log IP masking.</summary>
        public string LogMaskAddress { get; set; } = string.Empty;

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

        // ── Custom routing rules ──────────────────────────────────────────────
        /// <summary>User-defined routing rules. Applied only when RoutingMode == "smart".</summary>
        public List<CustomRoutingRule>? CustomRules { get; set; }

        // ── Subscriptions ─────────────────────────────────────────────────────
        /// <summary>Persisted subscription sources. Nodes derived from these carry SubscriptionId = the entry's Id.</summary>
        public List<SubscriptionEntry>? Subscriptions { get; set; }
    }
}
