namespace XrayUI.Models
{
    public class AppSettings
    {
        public int LocalSocksPort { get; set; } = 16890;
        public int LocalHttpPort  { get; set; } = 16891;
        /// <summary>"smart" | "global"</summary>
        public string RoutingMode { get; set; } = "smart";
        /// <summary>TUN 模式是否已启用</summary>
        public bool IsTunMode { get; set; } = false;
        public string? LastTunServerHost { get; set; }
        public bool IsStartupEnabled { get; set; } = false;
        public bool IsAutoConnect    { get; set; } = false;
        public string? LastAutoConnectServerName { get; set; }

        // ── Personalization ───────────────────────────────────────────────────
        /// <summary>"Light" | "Dark" | "Default" (follows system)</summary>
        public string? ThemeSetting { get; set; }
        public string? ColorSs        { get; set; }
        public string? ColorVless     { get; set; }
        public string? ColorVmess     { get; set; }
        public string? ColorHysteria2 { get; set; }
        public string? ColorFallback  { get; set; }
    }
}
