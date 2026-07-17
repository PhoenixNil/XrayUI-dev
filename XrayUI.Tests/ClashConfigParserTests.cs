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
        public void Parse_MissingHostOrPort_IsSkipped()
        {
            var yaml = """
                proxies:
                  - { name: no-port, type: ss, server: ss.example.com, cipher: aes-256-gcm, password: pw }
                  - { name: no-host, type: ss, port: 8388, cipher: aes-256-gcm, password: pw }
                """;

            var result = ClashConfigParser.Parse(yaml);

            Assert.Empty(result.Nodes);
            Assert.Equal(2, result.Skipped);
        }

        [Fact]
        public void Parse_InvalidYaml_Throws()
        {
            Assert.ThrowsAny<Exception>(() => ClashConfigParser.Parse("proxies:\n  - {unclosed"));
        }
    }
}
