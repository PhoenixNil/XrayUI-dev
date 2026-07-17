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
        [InlineData("socks5://user:pass@example.com:1080")]
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

        [Fact]
        public void Parse_SsWithSip003Plugin_ReturnsNull()
        {
            // xray-core has no SIP003 outbound; importing would save an unconnectable node.
            var link = $"ss://{B64("aes-256-gcm:pw")}@example.com:8388?plugin=v2ray-plugin%3Btls#X";

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
    }
}
