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
        public void RoundTrip_Hysteria2PortHoppingAndPin()
        {
            RoundTrip("hysteria2://87ae2e88-068e-48bc-a785-b98047a308e7@167.234.251.78:35000" +
                      "?sni=www.apple.com" +
                      "&pinSHA256=88b874b4cee4f3b6ca00a629b71447c9b8dc9a12056ce6f8cba772fff80b3d02" +
                      "&mport=35000-39000#hy2");
        }

        [Fact]
        public void Share_Hysteria2PortHopping_EmitsMportNotFmBlob()
        {
            // Folding mport into finalmask is only worth it if it comes back out as mport= —
            // a v2rayN user pasting our link has to get port hopping, not an opaque fm= blob.
            var s = NodeLinkParser.Parse("hysteria2://pw@hy2.example.com:35000" +
                                         "?sni=x.example.com&pinSHA256=88b874b4&mport=35000-39000#H2");
            Assert.NotNull(s);

            var link = NodeLinkSerializer.ToLink(s);

            Assert.NotNull(link);
            Assert.Contains("mport=35000-39000", link);
            Assert.Contains("pinSHA256=88b874b4", link);
            Assert.DoesNotContain("fm=", link);
        }

        [Fact]
        public void RoundTrip_Hysteria2UdpHopWithoutInterval()
        {
            // A hand-written hop carrying only ports is NOT what the writer emits, so it must
            // survive in fm= rather than being collapsed into mport= — collapsing it would make
            // the re-import invent interval:"30" that the author never wrote.
            var fm = Uri.EscapeDataString("""{"quicParams":{"udpHop":{"ports":"35000-39000"}}}""");
            RoundTrip($"hysteria2://pw@hy2.example.com:443?sni=hy2.example.com&fm={fm}#H2");
        }

        [Fact]
        public void RoundTrip_Hysteria2NonCanonicalUdpHop()
        {
            // A hop interval the parser would never write must survive the share hop: the
            // serializer has to leave it in fm= instead of collapsing it into mport=.
            var fm = Uri.EscapeDataString("""{"quicParams":{"congestion":"brutal","udpHop":{"ports":"35000-39000","interval":"5"}}}""");
            RoundTrip($"hysteria2://pw@hy2.example.com:443?sni=hy2.example.com&fm={fm}#H2");
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
        public void RoundTrip_AnyTls()
        {
            RoundTrip("anytls://p%40ss@a.example.com:8443?sni=real.example.com&insecure=1#AnyTLS%20%E8%8A%82%E7%82%B9");
        }

        [Fact]
        public void RoundTrip_AnyTlsWithFingerprintAndPin()
        {
            // Only fixture exercising the fp/hpkp extensions past the two-param scheme — without
            // it the round-trip guard compares default-vs-default and the serializer emit could drop.
            RoundTrip("anytls://pw123@a.example.com:443" +
                      "?sni=real.example.com&fp=chrome" +
                      "&hpkp=88b874b4cee4f3b6ca00a629b71447c9b8dc9a12056ce6f8cba772fff80b3d02&insecure=1#AT");
        }

        [Fact]
        public void Share_AnyTlsPin_EmitsHpkpForMihomo()
        {
            // mihomo reads an anytls pin only from hpkp; under any other key it imports an
            // insecure=1 link with no pin, i.e. with no certificate verification at all.
            var s = NodeLinkParser.Parse("anytls://pw@a.example.com:443" +
                                         "?sni=real.example.com&insecure=1&pinSHA256=88b874b4#AT");
            Assert.NotNull(s);

            var link = NodeLinkSerializer.ToLink(s);

            Assert.NotNull(link);
            Assert.Contains("hpkp=88b874b4", link);
            Assert.Contains("insecure=1", link);
            Assert.DoesNotContain("pinSHA256", link);
        }

        [Fact]
        public void RoundTrip_AnyTlsReality()
        {
            // anytls over REALITY (security=reality + pbk/sid) must not lose the reality keys —
            // exactly the field-dropping bug this fixture guards against.
            RoundTrip("anytls://c3ludGhldGljLWFueXRscw%3D%3D@203.0.113.7:44300" +
                      "?security=reality&sni=www.example.com&fp=chrome" +
                      "&pbk=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8&sid=0123456789abcdef#anytls");
        }

        [Fact]
        public void RoundTrip_Ipv6Host()
        {
            RoundTrip("vless://u-u-i-d@[2001:db8::1]:443?type=tcp&security=none&encryption=none#V6");
        }

        [Fact]
        public void RoundTrip_Socks5WithAuthAndSpecialChars()
        {
            RoundTrip("socks5://user%3A1:p%40ss%2F1%3F2%233@proxy.example.com:1080#SOCKS5%20%E8%8A%82%E7%82%B9");
        }

        [Fact]
        public void RoundTrip_Socks5NoAuth()
        {
            RoundTrip("socks5://1.2.3.4:1080#S5");
        }

        [Fact]
        public void RoundTrip_Socks5PartialCredentials()
        {
            RoundTrip("socks5://alice:@1.2.3.4:1080#UserOnly");
            RoundTrip("socks5://:secret@1.2.3.4:1080#PassOnly");
        }

        [Fact]
        public void RoundTrip_Socks5Ipv6()
        {
            RoundTrip("socks5://alice:p%40ss%3A1@[2001:db8::1]:1080#S5%20V6");
        }

        [Fact]
        public void ToLink_UnsupportedOrIncompleteProtocols_ReturnNull()
        {
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "wireguard", Host = "h", Port = 1 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "chain", Host = "h", Port = 1 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "socks", Host = "", Port = 1080 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "socks", Host = "h", Port = 0 }));
            Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = "http", Host = "h", Port = 8080 }));
        }
    }
}
