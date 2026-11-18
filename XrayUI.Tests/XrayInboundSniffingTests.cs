using System.Text.Json.Nodes;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class XrayInboundSniffingTests
    {
        [Fact]
        public void Create_DefaultSniffsHttpTlsAndQuic()
        {
            var sniffing = XrayInboundSniffing.Create();
            var expected = JsonNode.Parse("""{"enabled":true,"destOverride":["http","tls","quic"]}""");

            Assert.True(JsonNode.DeepEquals(expected, sniffing), sniffing.ToJsonString());
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void Create_UsesRouteOnlyForLeakGuardWithoutFakeDns(
            bool leakGuardActive, bool fakeDnsEnabled, bool expectedRouteOnly)
        {
            var sniffing = XrayInboundSniffing.Create(
                fakeDnsEnabled: fakeDnsEnabled,
                leakGuardActive: leakGuardActive);

            if (expectedRouteOnly)
                Assert.True((bool?)sniffing["routeOnly"]);
            else
                Assert.False(sniffing.ContainsKey("routeOnly"));

            Assert.Equal(fakeDnsEnabled ? false : (bool?)null, (bool?)sniffing["metadataOnly"]);
            string[] expectedOverrides = fakeDnsEnabled ? ["fakedns", "http", "tls", "quic"] : ["http", "tls", "quic"];
            Assert.Equal(expectedOverrides, sniffing["destOverride"]!.AsArray().Select(p => p!.GetValue<string>()));
        }
    }
}
