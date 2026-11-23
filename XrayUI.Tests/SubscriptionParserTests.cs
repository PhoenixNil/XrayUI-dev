using System.Text;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class SubscriptionParserTests
    {
        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        [Fact]
        public void Parse_PlainTextSocks5List_ParsesAllNodesAndIgnoresHttp()
        {
            var body = string.Join("\n",
                "socks5://user1:pass1@1.2.3.4:1080#SOCKS5-1",
                "http://user:pass@9.9.9.9:8080#HTTP-Ignored",
                "socks5://5.6.7.8:1081");

            var nodes = SubscriptionParser.Parse(body);

            Assert.Equal(2, nodes.Count);

            Assert.Equal("socks", nodes[0].Protocol);
            Assert.Equal("1.2.3.4", nodes[0].Host);
            Assert.Equal(1080, nodes[0].Port);
            Assert.Equal("user1", nodes[0].Username);
            Assert.Equal("pass1", nodes[0].Password);
            Assert.Equal("SOCKS5-1", nodes[0].Name);

            Assert.Equal("socks", nodes[1].Protocol);
            Assert.Equal("5.6.7.8", nodes[1].Host);
            Assert.Equal(1081, nodes[1].Port);
            Assert.Equal(string.Empty, nodes[1].Name);
        }

        [Fact]
        public void Parse_Base64Subscription_ParsesAllNodes()
        {
            var plain = string.Join("\r\n",
                "socks5://u:p@socks.example.com:1080#SOCKS-Sub",
                "vless://b831381d-6324-4d53-ad4f-8cda48b30811@v.example.com:443?type=tcp&security=none#VLESS-Sub");

            var nodes = SubscriptionParser.Parse(B64(plain));

            Assert.Equal(2, nodes.Count);
            Assert.Equal("socks", nodes[0].Protocol);
            Assert.Equal("vless", nodes[1].Protocol);
        }

        [Fact]
        public void Parse_UrlSafeUnpaddedBase64WithLineBreaksAndBom_ParsesAllNodes()
        {
            var plain = "\uFEFFsocks5://user:%3F%3F@1.2.3.4:1080#S5-HK\nsocks5://user:>>@5.6.7.8:1080#S5-JP";
            var urlSafeUnpadded = B64(plain)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            // Simulate MIME line wrapping plus leading BOM.
            var wrapped = "\uFEFF" + urlSafeUnpadded[..20] + "\r\n" + urlSafeUnpadded[20..];

            var nodes = SubscriptionParser.Parse(wrapped);

            Assert.Equal(2, nodes.Count);
            Assert.Equal("??", nodes[0].Password);
            Assert.Equal(">>", nodes[1].Password);
        }

        [Fact]
        public void Parse_ClashYaml_ImportsSocks5SkipsHttpAndIgnoresHealthcheckUrls()
        {
            // The folded health-check URL sits on a line of its own; it must never become a node.
            var yaml = """
                port: 7890
                proxies:
                  - { name: S1, type: socks5, server: 5.6.7.8, port: 1080, username: u, password: p }
                  - { name: H1, type: http, server: 1.2.3.4, port: 8080 }
                proxy-groups:
                  - name: Auto
                    type: url-test
                    url: >-
                      http://status.example:8080/
                    proxies: [S1, H1]
                """;

            var node = Assert.Single(SubscriptionParser.Parse(yaml));
            Assert.Equal("S1", node.Name);
            Assert.Equal("socks", node.Protocol);

            // Quoted keys are the same YAML document.
            var quotedKeys = yaml.Replace("proxies:", "\"proxies\":");
            Assert.Equal("S1", Assert.Single(SubscriptionParser.Parse(quotedKeys)).Name);

            // Even when every proxy is skipped, the health-check URL must not stand in for them.
            var allSkipped = yaml.Replace("password: p }", "password: p, tls: true }");
            Assert.Empty(SubscriptionParser.Parse(allSkipped));
        }

        [Fact]
        public void Parse_Base64ClashYamlSubscription_ParsesSocks5()
        {
            var yaml = """
                proxies:
                  - { name: "S1", type: socks5, server: 5.6.7.8, port: 1080, username: u, password: p }
                """;

            var node = Assert.Single(SubscriptionParser.Parse(B64(yaml)));
            Assert.Equal("socks", node.Protocol);
            Assert.Equal("u", node.Username);
        }

        [Fact]
        public void Parse_Base64ListWithInvalidUtf8InOneName_KeepsAllNodes()
        {
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("socks5://1.2.3.4:1080#A"));
            bytes.Add(0xFF);
            bytes.AddRange(Encoding.ASCII.GetBytes("\nsocks5://5.6.7.8:1081#B"));

            var nodes = SubscriptionParser.Parse(Convert.ToBase64String(bytes.ToArray()));

            Assert.Equal(2, nodes.Count);
            Assert.Equal("1.2.3.4", nodes[0].Host);
            Assert.Equal("B", nodes[1].Name);
        }

        [Fact]
        public void Parse_PlainWebUrlsAndHtmlErrorPage_ReturnsEmpty()
        {
            var html = """
                <!DOCTYPE html>
                <html>
                <body>
                http://example.com
                http://1.2.3.4:8080/
                http://1.2.3.4:8080/?token=abc
                https://example.com:443
                </body>
                </html>
                """;

            Assert.Empty(SubscriptionParser.Parse(html));
            Assert.Empty(SubscriptionParser.Parse(string.Empty));
            Assert.Empty(SubscriptionParser.Parse("   "));
        }
    }
}
