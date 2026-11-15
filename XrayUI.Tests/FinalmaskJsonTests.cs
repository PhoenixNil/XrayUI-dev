using System.Text.Json.Nodes;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class FinalmaskJsonTests
    {
        private static JsonObject Translate(string mask)
        {
            var root = (JsonObject)FinalmaskJson.Parse(mask)!;
            FinalmaskJson.TranslateLegacyUdpHop(root);
            return root;
        }

        [Fact]
        public void TranslateLegacyUdpHop_WriterShape_BecomesLastUdpMask()
        {
            // The shape AddHysteria2UdpHop + AddHysteria2SalamanderMask store for a link carrying
            // mport= and obfs=. The hop has to land after salamander: the other order passes
            // -test and then fails every connection with "incorrect index".
            var mask = FinalmaskJson.AddHysteria2UdpHop(
                FinalmaskJson.AddHysteria2SalamanderMask(string.Empty, "obfs-pw"), "35000-39000");

            var root = Translate(mask);

            Assert.Null(root["quicParams"]);
            var udp = Assert.IsType<JsonArray>(root["udp"]);
            Assert.Equal(2, udp.Count);
            Assert.Equal("salamander", udp[0]!["type"]!.GetValue<string>());
            Assert.Equal("udphop", udp[1]!["type"]!.GetValue<string>());

            var settings = udp[1]!["settings"]!;
            Assert.Equal("intervalLocal,intervalRemote", settings["mode"]!.GetValue<string>());
            Assert.Equal("35000-39000", settings["remotePorts"]!.GetValue<string>());
            Assert.Equal("30", settings["interval"]!.GetValue<string>());
        }

        [Fact]
        public void TranslateLegacyUdpHop_KeepsOtherQuicParamsAndOmittedInterval()
        {
            var root = Translate("""{"quicParams":{"congestion":"bbr","udpHop":{"ports":"35000-39000"}}}""");

            Assert.Equal("bbr", root["quicParams"]!["congestion"]!.GetValue<string>());
            Assert.Null(root["quicParams"]!["udpHop"]);
            // No interval written means none emitted: the core's own default applies.
            Assert.Null(root["udp"]![0]!["settings"]!["interval"]);
        }

        [Fact]
        public void TranslateLegacyUdpHop_NativeHopAlreadyPresent_Wins()
        {
            var root = Translate(
                """{"udp":[{"type":"udphop","settings":{"mode":"perConnRemote","remotePorts":"100-200"}}],"quicParams":{"udpHop":{"ports":"35000-39000","interval":"30"}}}""");

            var udp = Assert.IsType<JsonArray>(root["udp"]);
            Assert.Single(udp);
            Assert.Equal("100-200", udp[0]!["settings"]!["remotePorts"]!.GetValue<string>());
            Assert.Null(root["quicParams"]);
        }

        [Fact]
        public void TranslateLegacyUdpHop_NoLegacyHop_LeavesMaskUntouched()
        {
            const string mask = """{"udp":[{"type":"salamander","settings":{"password":"pw"}}],"quicParams":{"congestion":"bbr"}}""";

            Assert.Equal(JsonNode.Parse(mask)!.ToJsonString(), Translate(mask).ToJsonString());
        }
    }
}
