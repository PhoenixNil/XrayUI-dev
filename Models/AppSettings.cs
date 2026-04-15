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
    }
}
