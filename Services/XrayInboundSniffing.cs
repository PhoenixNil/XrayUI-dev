using System.Text.Json.Nodes;

namespace XrayUI.Services
{
    internal static class XrayInboundSniffing
    {
        public static JsonObject Create(bool fakeDnsEnabled = false, bool leakGuardActive = false)
        {
            var protocols = new JsonArray("http", "tls", "quic");
            var sniffing = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = protocols
            };

            if (fakeDnsEnabled)
            {
                protocols.Insert(0, XrayConfigConstants.FakeDnsServerTag);
                sniffing["metadataOnly"] = false;
            }
            else if (leakGuardActive)
            {
                // Use the sniffed domain for routing and keep the original IP for dialing.
                sniffing["routeOnly"] = true;
            }

            return sniffing;
        }
    }
}