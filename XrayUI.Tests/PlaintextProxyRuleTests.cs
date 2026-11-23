using System.Text.Json.Nodes;
using XrayUI.Services;

namespace XrayUI.Tests
{
    // The address cases and the security/encryption/Trojan shapes below were checked against the
    // shipped 26.11.9 core with `xray run -test` on a plaintext outbound. The "left to the core"
    // addresses are refused by the core too; the rule answers false for them on purpose, since it
    // cannot read them exactly the way Go does, and the probe then lets the core decide.
    public class PlaintextProxyRuleTests
    {
        private const string Uuid = "11111111-2222-3333-4444-555555555555";
        // A throwaway key from `xray vlessenc`; the core loads a plaintext outbound carrying it.
        private const string MlkemEncryption = "mlkem768x25519plus.native.0rtt.ZQFlibt7yryK1_m3OCHaGvzKgXgAIRnMV3iBvt1s4ng";

        // The shapes XrayConfigBuilder emits: VLESS in the vnext form, Trojan in the flat form.
        private static JsonObject Vless(string address, string security = "none", string encryption = "none") => new()
        {
            ["tag"] = "out-0",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = address,
                    ["port"] = 443,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = Uuid, ["encryption"] = encryption }),
                }),
            },
            ["streamSettings"] = new JsonObject { ["network"] = "tcp", ["security"] = security },
        };

        private static JsonObject Trojan(string address, string security = "none") => new()
        {
            ["tag"] = "out-0",
            ["protocol"] = "trojan",
            ["settings"] = new JsonObject { ["address"] = address, ["port"] = 443, ["password"] = "p" },
            ["streamSettings"] = new JsonObject { ["network"] = "tcp", ["security"] = security },
        };

        [Theory]
        [InlineData("1.1.1.1")]
        [InlineData("100.64.0.1")]             // shared address space: not private to Go's IP.IsPrivate
        [InlineData("0.0.0.0")]
        [InlineData("172.32.0.1")]             // just past 172.16.0.0/12
        [InlineData("::10.0.0.1")]             // IPv4-compatible, not IPv4-mapped: stays IPv6
        [InlineData("2001:4860:4860::8888")]
        [InlineData("[2001:4860:4860::8888]")]
        [InlineData("example.com")]
        [InlineData("example.com.")]
        [InlineData("localhost.com")]
        [InlineData("a.notlan")]               // suffix must start at a dot
        [InlineData("1router")]                // dotless, but a dotless name must start with a letter
        public void PublicAddress_IsRefused(string address)
        {
            Assert.True(PlaintextProxyRule.IsRefused(Vless(address)));
            Assert.True(PlaintextProxyRule.IsRefused(Trojan(address)));
        }

        [Theory]
        [InlineData("10.0.0.1")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.255")]
        [InlineData("192.168.1.1")]
        [InlineData("127.0.0.1")]
        [InlineData("169.254.1.1")]
        [InlineData("::1")]
        [InlineData("[::1]")]
        [InlineData("fd00::1")]
        [InlineData("fe80::1")]
        [InlineData("::ffff:10.0.0.1")]
        [InlineData("router")]
        [InlineData("a-b")]
        [InlineData("nas.lan")]
        [InlineData("host.lan.")]
        [InlineData("printer.local")]
        [InlineData("LOCALHOST")]
        [InlineData("box.home.arpa")]
        [InlineData("svc.internal")]
        [InlineData("my.example")]
        [InlineData("foo.test")]
        [InlineData("x.invalid")]
        public void PrivateAddress_IsKept(string address)
        {
            Assert.False(PlaintextProxyRule.IsRefused(Vless(address)));
            Assert.False(PlaintextProxyRule.IsRefused(Trojan(address)));
        }

        [Theory]
        [InlineData("01.2.3.4")]               // Go rejects leading zeros and reads a domain
        [InlineData("1.2.3")]                  // .NET would read 1.2.0.3
        [InlineData("fe80::1%eth0")]           // Go rejects zones
        [InlineData("[1.1.1.1]")]
        [InlineData(" 1.1.1.1")]
        [InlineData("例子.com")]
        [InlineData("env:XRAY_SERVER")]
        [InlineData("")]
        public void UnsureAddress_IsLeftToTheCore(string address)
        {
            Assert.False(PlaintextProxyRule.IsRefused(Vless(address)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("NONE")]
        public void NoneSecurity_AnyCase_IsRefused(string security)
        {
            Assert.True(PlaintextProxyRule.IsRefused(Vless("1.1.1.1", security)));
        }

        [Fact]
        public void MissingStreamSettings_IsRefused()
        {
            var outbound = Vless("1.1.1.1");
            outbound.Remove("streamSettings");
            Assert.True(PlaintextProxyRule.IsRefused(outbound));
        }

        [Theory]
        [InlineData("tls")]
        [InlineData("reality")]
        [InlineData("xtls")]                   // the core fails on its own, for another reason
        public void OtherSecurity_IsKept(string security)
        {
            Assert.False(PlaintextProxyRule.IsRefused(Vless("1.1.1.1", security)));
            Assert.False(PlaintextProxyRule.IsRefused(Trojan("1.1.1.1", security)));
        }

        [Fact]
        public void VlessEncryption_IsKept()
        {
            Assert.False(PlaintextProxyRule.IsRefused(Vless("1.1.1.1", encryption: MlkemEncryption)));
        }

        [Theory]
        [InlineData("vmess")]
        [InlineData("shadowsocks")]
        [InlineData("hysteria")]
        [InlineData("VLESS")]                  // the core's protocol names are lowercase
        public void OtherProtocol_IsKept(string protocol)
        {
            var outbound = Vless("1.1.1.1");
            outbound["protocol"] = protocol;
            Assert.False(PlaintextProxyRule.IsRefused(outbound));
        }

        // Shapes XrayConfigBuilder does not emit are left to the core, even ones it refuses: the
        // flat VLESS and Trojan "servers" outbounds below are plaintext to a public address.
        [Fact]
        public void UnemittedShape_IsLeftToTheCore()
        {
            var twoUsers = Vless("1.1.1.1");
            ((JsonArray)twoUsers["settings"]!["vnext"]![0]!["users"]!)
                .Add(new JsonObject { ["id"] = Uuid, ["encryption"] = MlkemEncryption });

            var flatVless = Vless("1.1.1.1");
            flatVless["settings"] = new JsonObject
            {
                ["address"] = "1.1.1.1", ["port"] = 443, ["id"] = Uuid, ["encryption"] = "none",
            };

            // The core reads a flat "address" in place of vnext; this one is private.
            var flatOverVnext = Vless("1.1.1.1");
            ((JsonObject)flatOverVnext["settings"]!)["address"] = "10.0.0.1";

            var trojanServers = Trojan("1.1.1.1");
            trojanServers["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["address"] = "1.1.1.1", ["port"] = 443, ["password"] = "p" }),
            };

            Assert.False(PlaintextProxyRule.IsRefused(twoUsers));
            Assert.False(PlaintextProxyRule.IsRefused(flatVless));
            Assert.False(PlaintextProxyRule.IsRefused(flatOverVnext));
            Assert.False(PlaintextProxyRule.IsRefused(trojanServers));
        }
    }
}
