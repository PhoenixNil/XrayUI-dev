namespace XrayUI.Services
{
    /// <summary>
    /// Inbound/outbound tags and well-known IP pools referenced by the xray-core JSON config.
    /// These strings are part of the contract with xray itself (they appear in routing rules,
    /// dns.servers entries, etc.), so changing them requires keeping every cross-reference in sync.
    /// </summary>
    public static class XrayConfigConstants
    {
        // Inbound / outbound / DNS tags
        public const string TunInboundTag    = "tun-in";
        public const string MixedInboundTag  = "mixed-in";
        public const string DnsOutboundTag   = "dns-out";
        public const string FakeDnsServerTag = "fakedns";

        // Outbound tags. These are the tags XrayUI injects into every config, generated or
        // hand-written, so a config profile's routing rules may reference them: ConfigProfileJson
        // validates rule targets against this set.
        public const string ProxyOutboundTag      = "proxy";
        public const string DirectOutboundTag     = "direct";
        public const string BlockOutboundTag      = "block";
        public const string ChainEntryOutboundTag = "chain-entry";

        // FakeDNS IP pools. 198.18.0.0/15 is RFC-2544 benchmarking space (safe to reuse).
        public const string FakeDnsPoolV4 = "198.18.0.0/15";
        public const string FakeDnsPoolV6 = "fc00::/18";

        // TUN adapter: the inbound name in the xray config must match the Windows
        // interface alias used by TunService for adapter operations (DNS reset, route delete).
        public const string TunInterfaceName        = "xray-tun";
        public const string TunOutboundInterfaceAuto = "auto";

        // TUN adapter addressing. xray assigns these to the xray-tun adapter and, via
        // autoSystemRoutingTable, hijacks the matching default route when run elevated.
        // The v6 gateway sits in fd00::/8, deliberately clear of the FakeDNS v6 pool (fc00::/18).
        public const string TunGatewayV4   = "172.18.0.1/30";
        public const string TunGatewayV6   = "fdfe:dcba:9876::1/64";
        public const string TunAutoRouteV4 = "0.0.0.0/0";
        public const string TunAutoRouteV6 = "::/0";

        public const int    TunMtuMin               = 68;
        public const int    TunMtuMax               = 9000;
        public const int    TunMtuDefault           = 1500;

        public static int NormalizeTunMtu(int mtu) =>
            mtu >= TunMtuMin && mtu <= TunMtuMax ? mtu : TunMtuDefault;
    }
}
