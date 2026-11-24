using XrayUI.Services;

namespace XrayUI.Tests
{
    public class ClashConfigParserTests
    {
        [Fact]
        public void Parse_NoProxiesSection_ReturnsEmptyResult()
        {
            var result = ClashConfigParser.Parse("port: 7890\nmode: rule\n");

            Assert.Empty(result.Nodes);
            Assert.Equal(0, result.Skipped);
        }

        [Fact]
        public void Parse_SupportedAndUnsupportedTypes_MapsAndCountsSkipped()
        {
            var yaml = """
                proxies:
                  - { name: "SS-1", type: ss, server: ss.example.com, port: 8388, cipher: aes-256-gcm, password: pw1 }
                  - { name: "Snell-1", type: snell, server: sn.example.com, port: 44046, psk: xxx }
                  - { name: "Tuic-1", type: tuic, server: tu.example.com, port: 443 }
                rules:
                  - MATCH,DIRECT
                """;

            var result = ClashConfigParser.Parse(yaml);

            var node = Assert.Single(result.Nodes);
            Assert.Equal(2, result.Skipped);
            Assert.Equal("SS-1", node.Name);
            Assert.Equal("ss", node.Protocol);
            Assert.Equal("ss.example.com", node.Host);
            Assert.Equal(8388, node.Port);
            Assert.Equal("aes-256-gcm", node.Encryption);
            Assert.Equal("pw1", node.Password);
        }

        [Fact]
        public void Parse_SsWithPlugin_IsSkipped()
        {
            var yaml = """
                proxies:
                  - name: obfs-node
                    type: ss
                    server: ss.example.com
                    port: 8388
                    cipher: aes-256-gcm
                    password: pw
                    plugin: obfs
                    plugin-opts: { mode: tls }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(1, result.Skipped);
        }

        [Fact]
        public void Parse_VmessWs_MapsTransportFromWsOpts()
        {
            var yaml = """
                proxies:
                  - name: vm-ws
                    type: vmess
                    server: vm.example.com
                    port: 443
                    uuid: b831381d-6324-4d53-ad4f-8cda48b30811
                    alterId: 0
                    cipher: auto
                    tls: true
                    servername: sni.example.com
                    skip-cert-verify: true
                    network: ws
                    ws-opts:
                      path: /ws
                      headers:
                        Host: cdn.example.com
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("vmess", node.Protocol);
            Assert.Equal("ws", node.Network);
            Assert.Equal("/ws", node.Path);
            Assert.Equal("cdn.example.com", node.WsHost);
            Assert.Equal("tls", node.Security);
            Assert.Equal("sni.example.com", node.Sni);
            Assert.True(node.AllowInsecure);
            Assert.Equal("TLS", node.Encryption);
        }

        [Fact]
        public void Parse_VmessUnsupportedNetwork_IsSkipped()
        {
            var yaml = """
                proxies:
                  - { name: vm-h2, type: vmess, server: h.example.com, port: 443, uuid: u, network: h2 }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(1, result.Skipped);
        }

        [Fact]
        public void Parse_VlessReality_MapsRealityOpts()
        {
            var yaml = """
                proxies:
                  - name: vl-reality
                    type: vless
                    server: r.example.com
                    port: 443
                    uuid: b831381d-6324-4d53-ad4f-8cda48b30811
                    network: tcp
                    tls: true
                    servername: www.apple.com
                    client-fingerprint: chrome
                    flow: xtls-rprx-vision
                    reality-opts:
                      public-key: PBK123
                      short-id: 6ba85179
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("reality", node.Security);
            Assert.Equal("PBK123", node.PublicKey);
            Assert.Equal("6ba85179", node.ShortId);
            Assert.Equal("chrome", node.Fingerprint);
            Assert.Equal("xtls-rprx-vision", node.Flow);
            Assert.Equal("Reality", node.Encryption);
        }

        [Fact]
        public void Parse_TrojanGrpc_ServiceNameLandsOnPath()
        {
            var yaml = """
                proxies:
                  - name: tj-grpc
                    type: trojan
                    server: t.example.com
                    port: 443
                    password: pw
                    network: grpc
                    sni: t.example.com
                    grpc-opts:
                      grpc-service-name: svc
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("trojan", node.Protocol);
            Assert.Equal("grpc", node.Network);
            Assert.Equal("svc", node.Path);
        }

        [Fact]
        public void Parse_Hysteria2PortsAndFingerprint_BuildsUdpHopAndPin()
        {
            var yaml = """
                proxies:
                  - name: hy2hop
                    type: hysteria2
                    server: hy2.example.com
                    port: 35000
                    password: pw
                    ports: 35000-39000
                    hop-interval: 15-30
                    fingerprint: 88b874b4
                    sni: www.apple.com
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("hysteria2", node.Protocol);
            // Clash's `fingerprint` is the cert pin, not the uTLS `client-fingerprint`; stored
            // as given, like the link parser does.
            Assert.Equal("88b874b4", node.PinnedPeerCertSha256);
            Assert.Contains("udpHop", node.Finalmask);
            Assert.Contains("35000-39000", node.Finalmask);
            Assert.Contains("15-30", node.Finalmask);
        }

        [Fact]
        public void Parse_Hysteria2Salamander_BuildsFinalmask()
        {
            var yaml = """
                proxies:
                  - name: hy2
                    type: hysteria2
                    server: hy2.example.com
                    port: 443
                    password: pw
                    obfs: salamander
                    obfs-password: ob123
                    sni: hy2.example.com
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("hysteria2", node.Protocol);
            Assert.Contains("salamander", node.Finalmask);
            Assert.Contains("ob123", node.Finalmask);
        }

        [Fact]
        public void Parse_AnyTls_MapsTlsKnobs()
        {
            var yaml = """
                proxies:
                  - name: at-1
                    type: anytls
                    server: a.example.com
                    port: 8443
                    password: pw
                    sni: real.example.com
                    client-fingerprint: chrome
                    skip-cert-verify: true
                    fingerprint: 88b874b4
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("anytls", node.Protocol);
            Assert.Equal("a.example.com", node.Host);
            Assert.Equal(8443, node.Port);
            Assert.Equal("pw", node.Password);
            Assert.Equal("tcp", node.Network);
            Assert.Equal("tls", node.Security);
            Assert.Equal("real.example.com", node.Sni);
            // client-fingerprint is the uTLS fingerprint; `fingerprint` is the cert pin.
            Assert.Equal("chrome", node.Fingerprint);
            Assert.True(node.AllowInsecure);
            Assert.Equal("88b874b4", node.PinnedPeerCertSha256);
            Assert.Equal("TLS", node.Encryption);
        }

        [Fact]
        public void Parse_AnyTlsWithoutPassword_IsSkipped()
        {
            // The core refuses an anytls outbound without a password, so importing it would only
            // fail later.
            var yaml = """
                proxies:
                  - { name: at-missing, type: anytls, server: a.example.com, port: 443, sni: a.example.com }
                  - { name: at-empty, type: anytls, server: a.example.com, port: 443, password: "" }
                  - { name: at-ok, type: anytls, server: a.example.com, port: 443, password: pw }
                """;

            var result = ClashConfigParser.Parse(yaml);

            var node = Assert.Single(result.Nodes);
            Assert.Equal("at-ok", node.Name);
            Assert.Equal(2, result.Skipped);
        }

        [Fact]
        public void Parse_AnyTlsWithUnsupportedWrappers_AreSkipped()
        {
            // Xray has no ShadowTLS/ResTLS/JLS dialer; without the wrapper the node cannot connect,
            // so dropping the block would import a broken plain-TLS node instead of counting it
            // skipped.
            var yaml = """
                proxies:
                  - name: at-shadowtls
                    type: anytls
                    server: a.example.com
                    port: 443
                    password: pw
                    shadow-tls-opts: { version: 3, password: spw }
                  - name: at-restls
                    type: anytls
                    server: a.example.com
                    port: 443
                    password: pw
                    restls-opts: { password: rpw }
                  - name: at-jls
                    type: anytls
                    server: a.example.com
                    port: 443
                    password: pw
                    jls-opts: { password: jpw }
                  - name: at-plain
                    type: anytls
                    server: a.example.com
                    port: 443
                    password: pw
                """;

            var result = ClashConfigParser.Parse(yaml);

            var node = Assert.Single(result.Nodes);
            Assert.Equal("at-plain", node.Name);
            Assert.Equal(3, result.Skipped);
        }

        [Fact]
        public void Parse_WireguardSinglePeer_BuildsCidrLocalAddress()
        {
            var yaml = """
                proxies:
                  - name: wg
                    type: wireguard
                    server: wg.example.com
                    port: 51820
                    private-key: PRIV
                    public-key: PUB
                    ip: 172.16.0.2
                    ipv6: fd00::2
                    reserved: [1, 2, 3]
                    mtu: 1280
                """;

            var node = Assert.Single(ClashConfigParser.Parse(yaml).Nodes);

            Assert.Equal("wireguard", node.Protocol);
            Assert.Equal("PRIV", node.WgPrivateKey);
            Assert.Equal("PUB", node.WgPublicKey);
            Assert.Equal("172.16.0.2/32,fd00::2/128", node.WgLocalAddress);
            Assert.Equal("1,2,3", node.WgReserved);
            Assert.Equal(1280, node.WgMtu);
        }

        [Fact]
        public void Parse_Socks5_MapsAllFieldsAndHttpIsSkipped()
        {
            var yaml = """
                proxies:
                  - name: s5-auth
                    type: socks5
                    server: s5.example.com
                    port: 1080
                    username: alice
                    password: 123456
                    udp: true
                    tls: false
                  - name: s5-noauth
                    type: socks
                    server: 1.2.3.4
                    port: 1081
                  - name: http-auth
                    type: http
                    server: http.example.com
                    port: 8080
                    username: bob
                    password: secret
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Equal(1, result.Skipped);
            Assert.Equal(2, result.Nodes.Count);

            var s5 = result.Nodes[0];
            Assert.Equal("s5-auth", s5.Name);
            Assert.Equal("socks", s5.Protocol);
            Assert.Equal("s5.example.com", s5.Host);
            Assert.Equal(1080, s5.Port);
            Assert.Equal("alice", s5.Username);
            Assert.Equal("123456", s5.Password);
            Assert.Equal("tcp", s5.Network);
            Assert.Equal("none", s5.Security);
            Assert.Equal(string.Empty, s5.Encryption);

            var s5NoAuth = result.Nodes[1];
            Assert.Equal("socks", s5NoAuth.Protocol);
            Assert.Equal("1.2.3.4", s5NoAuth.Host);
            Assert.Equal(1081, s5NoAuth.Port);
            Assert.Equal(string.Empty, s5NoAuth.Username);
            Assert.Equal(string.Empty, s5NoAuth.Password);
        }

        [Fact]
        public void Parse_Socks5WithTls_IsSkipped()
        {
            var yaml = """
                proxies:
                  - { name: tls-true, type: socks5, server: s.example.com, port: 443, tls: true }
                  - { name: tls-int, type: socks5, server: s.example.com, port: 443, tls: 1 }
                  - { name: tls-str, type: socks5, server: s.example.com, port: 443, tls: "True" }
                  - { name: tls-t, type: socks5, server: s.example.com, port: 443, tls: t }
                  - { name: tls-unknown, type: socks5, server: s.example.com, port: 443, tls: maybe }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(5, result.Skipped);
        }

        [Fact]
        public void Parse_Socks5ExplicitlyPlaintext_IsImported()
        {
            var yaml = """
                proxies:
                  - { name: tls-false, type: socks5, server: s.example.com, port: 1080, tls: false }
                  - { name: tls-zero, type: socks5, server: s.example.com, port: 1080, tls: 0 }
                  - { name: tls-f, type: socks5, server: s.example.com, port: 1080, tls: "f" }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Equal(0, result.Skipped);
            Assert.Equal(["tls-false", "tls-zero", "tls-f"], result.Nodes.Select(n => n.Name));
        }

        [Fact]
        public void Parse_Socks5WithMergeKey_IsSkipped()
        {
            // The merge key could be inheriting tls: true; importing it as a plain outbound would
            // send the credentials in the clear.
            var yaml = """
                base: &secure
                  tls: true
                proxies:
                  - name: merged
                    type: socks5
                    server: s.example.com
                    port: 443
                    <<: *secure
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(1, result.Skipped);
        }

        [Fact]
        public void Parse_MissingHostOrPort_IsSkipped()
        {
            var yaml = """
                proxies:
                  - { name: no-port, type: ss, server: ss.example.com, cipher: aes-256-gcm, password: pw }
                  - { name: no-host, type: ss, port: 8388, cipher: aes-256-gcm, password: pw }
                  - { name: bad-port, type: socks5, server: s.example.com, port: 70000 }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(3, result.Skipped);
        }

        [Fact]
        public void Parse_InvalidYaml_Throws()
        {
            Assert.ThrowsAny<Exception>(() => ClashConfigParser.Parse("proxies:\n  - {unclosed"));
        }
    }
}
