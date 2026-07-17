using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Tests
{
    /// <summary>
    /// Parse → ToLink → Parse must be lossless for every persisted connection-config
    /// field. NodeLinkSerializer documents itself as the exact inverse of
    /// NodeLinkParser; these tests hold both to that contract.
    /// </summary>
    public class NodeLinkRoundTripTests
    {
        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        private static void RoundTrip(string link)
        {
            var first = NodeLinkParser.Parse(link);
            Assert.NotNull(first);

            var relink = NodeLinkSerializer.ToLink(first);
            Assert.NotNull(relink);

            var second = NodeLinkParser.Parse(relink);
            Assert.NotNull(second);

            AssertConfigEqual(first, second);
        }

        // Properties that are persisted but are identity/bookkeeping, not
        // connection config carried by a share link. Anything listed here is a
        // deliberate, visible exclusion from the round-trip invariant.
        private static readonly string[] ExcludedProperties =
        [
            nameof(ServerEntry.Id) // regenerated per instance on parse
        ];

        // Compares every writable, non-[JsonIgnore] property, so a future
        // persisted field is covered automatically the day it is added — if it
        // must not round-trip, it has to be excluded loudly above. Reflection
        // is fine here: the test project is not AOT-compiled.
        private static void AssertConfigEqual(ServerEntry a, ServerEntry b)
        {
            var mismatches = new List<string>();
            foreach (var p in typeof(ServerEntry).GetProperties())
            {
                if (!p.CanWrite || ExcludedProperties.Contains(p.Name)) continue;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;

                var va = p.GetValue(a);
                var vb = p.GetValue(b);
                if (!Equals(va, vb))
                    mismatches.Add($"{p.Name}: \"{va}\" != \"{vb}\"");
            }

            Assert.True(mismatches.Count == 0,
                "Round-trip lost fields:\n" + string.Join("\n", mismatches));
        }

        [Fact]
        public void RoundTrip_Ss()
        {
            RoundTrip($"ss://{B64("aes-256-gcm:p@ss:w0rd/=+")}@example.com:8388#SS%20%E8%8A%82%E7%82%B9");
        }

        [Fact]
        public void RoundTrip_VmessWsTlsInsecure()
        {
            var json = """
                {"v":"2","ps":"VM 节点","add":"vm.example.com","port":"443",
                 "id":"b831381d-6324-4d53-ad4f-8cda48b30811","aid":"1","net":"ws","type":"none",
                 "host":"cdn.example.com","path":"/ws?ed=2048","tls":"tls","sni":"sni.example.com",
                 "fp":"chrome","allowInsecure":"1"}
                """;
            RoundTrip($"vmess://{B64(json)}");
        }

        [Fact]
        public void RoundTrip_VlessRealityVision()
        {
            RoundTrip("vless://b831381d-6324-4d53-ad4f-8cda48b30811@example.com:443" +
                      "?type=tcp&security=reality&sni=www.apple.com&fp=chrome" +
                      "&pbk=PBK123&sid=6ba85179&spx=%2Fspider&flow=xtls-rprx-vision&encryption=none#Reality");
        }

        [Fact]
        public void RoundTrip_VlessXhttpModeAndExtra()
        {
            // The dogegg-style stream-one + xPadding combo: losing mode/extra on
            // round-trip is exactly the historical import-timeout bug.
            var extra = Uri.EscapeDataString("""{"xPaddingBytes":"100-1000","xmux":{"maxConcurrency":16}}""");
            RoundTrip("vless://u-u-i-d@example.com:8443" +
                      $"?type=xhttp&security=tls&sni=x.example.com&path=%2Fxh&host=h.example.com&mode=stream-one&extra={extra}#XHTTP");
        }

        [Fact]
        public void RoundTrip_VlessPostQuantumEncryption()
        {
            RoundTrip("vless://u-u-i-d@example.com:443" +
                      "?type=tcp&security=tls&sni=example.com&encryption=mlkem768x25519plus.native.0rtt.abc#PQ");
        }

        [Fact]
        public void RoundTrip_VlessTlsWithEch()
        {
            // Only fixture exercising EchConfigList/EchForceQuery — without it the
            // round-trip guard compares default-vs-default and the serializer's
            // ECH emit logic could be deleted without any test noticing.
            var ech = Uri.EscapeDataString("AEX+DQBBzQAgACBSvVUkExampleConfigList=");
            RoundTrip("vless://u-u-i-d@example.com:443" +
                      $"?type=tcp&security=tls&sni=example.com&encryption=none&echConfigList={ech}&echForceQuery=half#ECH");
        }

        [Fact]
        public void RoundTrip_Hysteria2WithSalamanderObfs()
        {
            RoundTrip("hysteria2://pw123@hy2.example.com:443" +
                      "?sni=hy2.example.com&insecure=1&obfs=salamander&obfs-password=ob123#H2");
        }

        [Fact]
        public void RoundTrip_TrojanGrpc()
        {
            RoundTrip("trojan://pw@t.example.com:443?type=grpc&sni=t.example.com&serviceName=svc#TG");
        }

        [Fact]
        public void RoundTrip_TrojanWs()
        {
            RoundTrip("trojan://p%40w@t.example.com:8443" +
                      "?type=ws&sni=t.example.com&path=%2Fws&host=cdn.t.example.com&allowInsecure=1#TW");
        }

        [Fact]
        public void RoundTrip_Ipv6Host()
        {
            RoundTrip("vless://u-u-i-d@[2001:db8::1]:443?type=tcp&security=none&encryption=none#V6");
        }

        [Fact]
        public void ToLink_UnsupportedProtocols_ReturnNull()
        {
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "wireguard", Host = "h", Port = 1 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "chain", Host = "h", Port = 1 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "socks", Host = "h", Port = 1 }));
        }
    }
}
