using System.Text;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class NodeLinkParserTests
    {
        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        // ── invalid input ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://example.com")]
        [InlineData("vmess://!!!not-base64!!!")]
        [InlineData("ss://ZZZZ")]
        public void Parse_InvalidOrUnsupportedInput_ReturnsNull(string? link)
        {
            Assert.Null(NodeLinkParser.Parse(link!));
        }

        // ── Shadowsocks ───────────────────────────────────────────────────────

        [Fact]
        public void Parse_SsSip002_ExtractsAllFields()
        {
            var link = $"ss://{B64("aes-256-gcm:p@ss:w0rd")}@example.com:8388#My%20Node";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("ss", s.Protocol);
            Assert.Equal("aes-256-gcm", s.Encryption);
            // Only the FIRST colon separates method from password.
            Assert.Equal("p@ss:w0rd", s.Password);
            Assert.Equal("example.com", s.Host);
            Assert.Equal(8388, s.Port);
            Assert.Equal("My Node", s.Name);
            Assert.Equal("tcp", s.Network);
        }

        [Fact]
        public void Parse_SsLegacyWholeBase64_ExtractsAllFields()
        {
            var link = $"ss://{B64("aes-128-gcm:secret@10.0.0.1:443")}#Legacy";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("aes-128-gcm", s.Encryption);
            Assert.Equal("secret", s.Password);
            Assert.Equal("10.0.0.1", s.Host);
            Assert.Equal(443, s.Port);
            Assert.Equal("Legacy", s.Name);
        }

        [Fact]
        public void Parse_SsRawPercentEncodedUserinfo_Ss2022KeySurvives()
        {
            // SS2022 keys carry '+' '/' '=' which generators percent-encode instead of base64-wrapping.
            var key = "8JmyO+3Sm2b1/2rDDDF8Tw==";
            var link = $"ss://{Uri.EscapeDataString($"2022-blake3-aes-256-gcm:{key}")}@example.com:8388#SS2022";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("2022-blake3-aes-256-gcm", s.Encryption);
            Assert.Equal(key, s.Password);
        }

        [Theory]
        [InlineData("")]
        [InlineData("/")]
        public void Parse_SsWithSip003Plugin_ReturnsNull(string separator)
        {
            // xray-core has no SIP003 outbound; importing would save an unconnectable node.
            var link = $"ss://{B64("aes-256-gcm:pw")}@example.com:8388{separator}?plugin=v2ray-plugin%3Btls#X";

            Assert.Null(NodeLinkParser.Parse(link));
        }

        [Theory]
        [InlineData("example.com", "example.com", "", false)]
        [InlineData("example.com", "example.com", "?group=Test%2FGroup", false)]
        [InlineData("[2001:db8::1]", "2001:db8::1", "?group=Test%2FGroup", false)]
        [InlineData("example.com", "example.com", "?group=Test%2FGroup", true)]
        [InlineData("[2001:db8::1]", "2001:db8::1", "", true)]
        public void Parse_SsOptionalSlash_PreservesCredentialsAndName(
            string authorityHost, string expectedHost, string query, bool rawUserinfo)
        {
            var method = rawUserinfo ? "2022-blake3-aes-256-gcm" : "aes-256-gcm";
            var password = rawUserinfo ? "8JmyO+3Sm2b1/2rDDDF8Tw==" : "p@ss:w0rd/+=?";
            var userinfo = rawUserinfo
                ? Uri.EscapeDataString($"{method}:{password}")
                : B64($"{method}:{password}");
            var link = $"ss://{userinfo}@{authorityHost}:8388/{query}#My%2FNode";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("ss", s.Protocol);
            Assert.Equal(method, s.Encryption);
            Assert.Equal(password, s.Password);
            Assert.Equal(expectedHost, s.Host);
            Assert.Equal(8388, s.Port);
            Assert.Equal("My/Node", s.Name);
        }

        [Theory]
        [InlineData("/path")]
        [InlineData("/path/")]
        [InlineData("//")]
        [InlineData("/path:443")]
        public void Parse_SsNonEmptyOrRepeatedPath_ReturnsNull(string path)
        {
            var link = $"ss://{B64("aes-256-gcm:pw")}@example.com:8388{path}?group=Test#Node";

            Assert.Null(NodeLinkParser.Parse(link));
        }

        // ── VMess ─────────────────────────────────────────────────────────────

        [Fact]
        public void Parse_VmessWsTls_ExtractsAllFields()
        {
            var json = """
                {"v":"2","ps":"VM Node","add":"vm.example.com","port":"443",
                 "id":"b831381d-6324-4d53-ad4f-8cda48b30811","aid":"0","net":"ws","type":"none",
                 "host":"cdn.example.com","path":"/ws","tls":"tls","sni":"sni.example.com","fp":"chrome"}
                """;

            var s = NodeLinkParser.Parse($"vmess://{B64(json)}");

            Assert.NotNull(s);
            Assert.Equal("vmess", s.Protocol);
            Assert.Equal("VM Node", s.Name);
            Assert.Equal("vm.example.com", s.Host);
            Assert.Equal(443, s.Port);
            Assert.Equal("b831381d-6324-4d53-ad4f-8cda48b30811", s.Uuid);
            Assert.Equal(0, s.AlterId);
            Assert.Equal("ws", s.Network);
            Assert.Equal("/ws", s.Path);
            Assert.Equal("cdn.example.com", s.WsHost);
            Assert.Equal("tls", s.Security);
            Assert.Equal("sni.example.com", s.Sni);
            Assert.Equal("chrome", s.Fingerprint);
            Assert.Equal("TLS", s.Encryption);
        }

        [Fact]
        public void Parse_VmessNumericPortAndAid_AreAccepted()
        {
            // Some generators emit port/aid as JSON numbers instead of strings.
            var json = """{"v":"2","ps":"n","add":"h.example.com","port":8443,"id":"u","aid":2,"net":"tcp","tls":""}""";

            var s = NodeLinkParser.Parse($"vmess://{B64(json)}");

            Assert.NotNull(s);
            Assert.Equal(8443, s.Port);
            Assert.Equal(2, s.AlterId);
            Assert.Equal("none", s.Security);
            Assert.Equal("None", s.Encryption);
        }

        // ── VLESS ─────────────────────────────────────────────────────────────

        [Fact]
        public void Parse_VlessRealityVision_ExtractsAllFields()
        {
            var link = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@example.com:443" +
                       "?type=tcp&security=reality&sni=www.apple.com&fp=chrome" +
                       "&pbk=PBK123&sid=6ba85179&spx=%2F&flow=xtls-rprx-vision&encryption=none#Reality%20Node";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("vless", s.Protocol);
            Assert.Equal("b831381d-6324-4d53-ad4f-8cda48b30811", s.Uuid);
            Assert.Equal("example.com", s.Host);
            Assert.Equal(443, s.Port);
            Assert.Equal("reality", s.Security);
            Assert.Equal("www.apple.com", s.Sni);
            Assert.Equal("chrome", s.Fingerprint);
            Assert.Equal("PBK123", s.PublicKey);
            Assert.Equal("6ba85179", s.ShortId);
            Assert.Equal("/", s.SpiderX);
            Assert.Equal("xtls-rprx-vision", s.Flow);
            // "encryption=none" maps to empty (the field stores only non-default values).
            Assert.Equal(string.Empty, s.VlessEncryption);
            Assert.Equal("Reality Node", s.Name);
            Assert.Equal("Reality", s.Encryption);
        }

        [Fact]
        public void Parse_VlessXhttp_KeepsModeAndExtra()
        {
            // Regression guard: dropping mode/extra produces a node that connects in
            // v2rayN but times out here (stream-one + xPadding obfuscation needs both).
            var extra = Uri.EscapeDataString("""{"xPaddingBytes":"100-1000"}""");
            var link = "vless://u-u-i-d@example.com:8443" +
                       $"?type=xhttp&security=tls&sni=x.example.com&path=%2Fxh&mode=stream-one&extra={extra}#XH";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("xhttp", s.Network);
            Assert.Equal("stream-one", s.XhttpMode);
            Assert.Contains("xPaddingBytes", s.XhttpExtra);
            Assert.Equal("/xh", s.Path);
        }

        [Fact]
        public void Parse_VlessGrpc_ModeParamIsNotMisreadAsXhttpMode()
        {
            // grpc links reuse "mode" for gun/multi — it must not leak into XhttpMode.
            var link = "vless://u-u-i-d@example.com:443?type=grpc&security=tls&serviceName=svc&mode=gun#G";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("grpc", s.Network);
            Assert.Equal(string.Empty, s.XhttpMode);
            Assert.Equal("svc", s.Path);
        }

        // ── Hysteria2 ─────────────────────────────────────────────────────────

        [Fact]
        public void Parse_Hysteria2WithSalamanderObfs_BuildsFinalmask()
        {
            var link = "hysteria2://pw123@hy2.example.com:443" +
                       "?sni=hy2.example.com&insecure=1&obfs=salamander&obfs-password=ob123#H2";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("hysteria2", s.Protocol);
            Assert.Equal("pw123", s.Password);
            Assert.Equal("hy2.example.com", s.Sni);
            Assert.True(s.AllowInsecure);
            Assert.Equal("udp", s.Network);
            Assert.Equal("tls", s.Security);
            Assert.Contains("salamander", s.Finalmask);
            Assert.Contains("ob123", s.Finalmask);
        }

        [Fact]
        public void Parse_Hysteria2WithMportAndPin_BuildsUdpHopAndPin()
        {
            // Shape emitted by v2rayN for a port-hopping node behind a masquerading serverName.
            var link = "hysteria2://87ae2e88-068e-48bc-a785-b98047a308e7@167.234.251.78:35000" +
                       "?sni=www.apple.com&insecure=0&allowInsecure=0" +
                       "&pinSHA256=88b874b4cee4f3b6ca00a629b71447c9b8dc9a12056ce6f8cba772fff80b3d02" +
                       "&mport=35000-39000#hy2";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal(35000, s.Port);
            Assert.False(s.AllowInsecure);
            Assert.Equal("88b874b4cee4f3b6ca00a629b71447c9b8dc9a12056ce6f8cba772fff80b3d02", s.PinnedPeerCertSha256);
            Assert.Contains("udpHop", s.Finalmask);
            Assert.Contains("35000-39000", s.Finalmask);
        }

        [Fact]
        public void Parse_Hysteria2PortRangeInAddress_DoesNotDropTheNode()
        {
            // hysteria2's native URI puts the hop range in the address. System.Uri rejects that
            // port outright, which used to make the whole node disappear at import.
            var s = NodeLinkParser.Parse("hysteria2://pw@hy2.example.com:35000-39000?sni=hy2.example.com#H2");

            Assert.NotNull(s);
            Assert.Equal("hy2.example.com", s.Host);
            Assert.Equal(35000, s.Port);
            Assert.Contains("35000-39000", s.Finalmask);
        }

        [Theory]
        // Stored as given. The shipped core accepts hex in either case and strips colons itself,
        // so rewriting what a link sent would only risk mangling it — separator cleanup belongs
        // on the hand-entry path in the edit dialog, which is where dirty pastes actually arrive.
        [InlineData("88b874b4cee4f3b6")]
        [InlineData("88B874B4CEE4F3B6")]
        [InlineData("88:b8:74:b4:ce:e4:f3:b6")]
        public void Parse_Hysteria2Pin_StoredVerbatim(string param)
        {
            var s = NodeLinkParser.Parse($"hysteria2://pw@hy2.example.com:443?pinSHA256={param}#H2");

            Assert.NotNull(s);
            Assert.Equal(param, s.PinnedPeerCertSha256);
        }

        [Fact]
        public void Parse_Hysteria2ExplicitFinalmaskHop_WinsOverMport()
        {
            // A hand-tuned udpHop in fm= must not be clobbered by the compact mport= form.
            var fm = Uri.EscapeDataString("""{"quicParams":{"udpHop":{"ports":"40000-41000","interval":"5"}}}""");
            var s = NodeLinkParser.Parse($"hysteria2://pw@hy2.example.com:443?fm={fm}&mport=35000-39000#H2");

            Assert.NotNull(s);
            Assert.Contains("40000-41000", s.Finalmask);
            Assert.DoesNotContain("35000-39000", s.Finalmask);
        }

        // ── Trojan ────────────────────────────────────────────────────────────

        [Fact]
        public void Parse_TrojanWs_ExtractsAllFields()
        {
            var link = "trojan://trojanpw@t.example.com:8443" +
                       "?sni=t.example.com&type=ws&path=%2Fws&host=cdn.t.example.com&fp=firefox#TJ";

            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("trojan", s.Protocol);
            Assert.Equal("trojanpw", s.Password);
            Assert.Equal("t.example.com", s.Host);
            Assert.Equal(8443, s.Port);
            Assert.Equal("ws", s.Network);
            Assert.Equal("/ws", s.Path);
            Assert.Equal("cdn.t.example.com", s.WsHost);
            Assert.Equal("tls", s.Security);
            Assert.Equal("firefox", s.Fingerprint);
        }

        [Fact]
        public void Parse_TrojanWithoutPort_DefaultsTo443()
        {
            var s = NodeLinkParser.Parse("trojan://pw@t.example.com#T");

            Assert.NotNull(s);
            Assert.Equal(443, s.Port);
        }

        [Fact]
        public void Parse_TrojanEmptyPassword_ReturnsNull()
        {
            Assert.Null(NodeLinkParser.Parse("trojan://@t.example.com:443#T"));
        }

        // ── SOCKS5 ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("socks5://user:pass@example.com:1080#My%20Socks")]
        [InlineData("SOCKS5://user:pass@example.com:1080#My%20Socks")]
        [InlineData("socks5://user:pass@example.com:1080/#My%20Socks")]
        public void Parse_SocksWithPlainAuth_ExtractsAllFields(string link)
        {
            var s = NodeLinkParser.Parse(link);

            Assert.NotNull(s);
            Assert.Equal("socks", s.Protocol);
            Assert.Equal("example.com", s.Host);
            Assert.Equal(1080, s.Port);
            Assert.Equal("user", s.Username);
            Assert.Equal("pass", s.Password);
            Assert.Equal("My Socks", s.Name);
            Assert.Equal("tcp", s.Network);
            Assert.Equal("none", s.Security);
            Assert.Equal(string.Empty, s.Encryption);
        }

        [Fact]
        public void Parse_SocksNoAuthAndEmptyUserinfo_ExtractsHostPort()
        {
            var plain = NodeLinkParser.Parse("socks5://1.2.3.4:1080");
            Assert.NotNull(plain);
            Assert.Equal("socks", plain.Protocol);
            Assert.Equal("1.2.3.4", plain.Host);
            Assert.Equal(1080, plain.Port);
            Assert.Equal(string.Empty, plain.Username);
            Assert.Equal(string.Empty, plain.Password);
            Assert.Equal(string.Empty, plain.Name);

            var emptyAt = NodeLinkParser.Parse("socks5://@1.2.3.4:1080#EmptyAt");
            Assert.NotNull(emptyAt);
            Assert.Equal(string.Empty, emptyAt.Username);
            Assert.Equal(string.Empty, emptyAt.Password);
            Assert.Equal("EmptyAt", emptyAt.Name);
        }

        [Fact]
        public void Parse_SocksSpecialCharactersInCredentials_ArePreserved()
        {
            var escaped = NodeLinkParser.Parse("socks5://user%3A1:p%40ss%2F2%3F%23@1.2.3.4:1080#Escaped");
            Assert.NotNull(escaped);
            Assert.Equal("user:1", escaped.Username);
            Assert.Equal("p@ss/2?#", escaped.Password);

            // A raw '@' inside the authority is tolerated: the host starts after the last one.
            var rawAtAndColon = NodeLinkParser.Parse("socks5://user:p@ss:123@1.2.3.4:1080#Raw");
            Assert.NotNull(rawAtAndColon);
            Assert.Equal("user", rawAtAndColon.Username);
            Assert.Equal("p@ss:123", rawAtAndColon.Password);
        }

        [Theory]
        [InlineData("user", "user", "")]
        [InlineData("alice:", "alice", "")]
        [InlineData(":secret", "", "secret")]
        public void Parse_SocksPartialCredentials_HandledWithoutMangling(
            string userinfo, string expectedUser, string expectedPass)
        {
            var s = NodeLinkParser.Parse($"socks5://{userinfo}@1.2.3.4:1080#Partial");

            Assert.NotNull(s);
            Assert.Equal(expectedUser, s.Username);
            Assert.Equal(expectedPass, s.Password);
        }

        [Theory]
        // Not supported: other schemes (v2rayN socks://, socks4://, socks5h://, http://).
        [InlineData("socks://user:pass@1.2.3.4:1080#V2rayN")]
        [InlineData("socks4://1.2.3.4:1080")]
        [InlineData("socks5h://user:pass@1.2.3.4:1080#Curl")]
        [InlineData("http://user:pass@1.2.3.4:8080#Http")]
        // Not standard socks5 URIs: whole-payload Base64, query parameters.
        [InlineData("socks5://MS4yLjMuNDoxMDgw#WholeB64")]
        [InlineData("socks5://YWxpY2U6c2VjcmV0QDEuMi4zLjQ6MTA4MA==#WholeB64Auth")]
        [InlineData("socks5://user:pass@1.2.3.4:1080?remarks=Node")]
        [InlineData("socks5://user:pass@1.2.3.4:1080/?tls=0")]
        [InlineData("socks5://user:pass@1.2.3.4:1080?tls=1#TLS")]
        // An unescaped '?' ends the authority, so it can't be part of a password.
        [InlineData("socks5://user:a?b=c@1.2.3.4:1080#QEq")]
        // Anything after host:port other than a lone '/'.
        [InlineData("socks5://1.2.3.4:1080/path")]
        [InlineData("socks5://docs.example/profiles/@cache.example:1080")]
        // Missing, malformed or out-of-range host:port.
        [InlineData("socks5://example.com")]
        [InlineData("socks5://example.com:0")]
        [InlineData("socks5://example.com:65536")]
        [InlineData("socks5://example.com:-1")]
        [InlineData("socks5://example.com:abc")]
        [InlineData("socks5://:1080")]
        [InlineData("socks5://2001:db8::1")]
        [InlineData("socks5://[2001:db8::1]")]
        [InlineData("socks5://[]:1080")]
        public void Parse_UnsupportedOrNonStandard_ReturnNull(string link)
        {
            Assert.Null(NodeLinkParser.Parse(link));
        }

        [Fact]
        public void Parse_SocksIpv6_Succeeds()
        {
            var s = NodeLinkParser.Parse("socks5://user:pass@[2001:db8::1]:1080/#V6");

            Assert.NotNull(s);
            Assert.Equal("socks", s.Protocol);
            Assert.Equal("2001:db8::1", s.Host);
            Assert.Equal(1080, s.Port);
            Assert.Equal("user", s.Username);
            Assert.Equal("pass", s.Password);
            Assert.Equal("V6", s.Name);
        }
    }
}
