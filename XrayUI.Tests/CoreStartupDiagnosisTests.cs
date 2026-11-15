using XrayUI.Services;

namespace XrayUI.Tests
{
    // Every log below is the "Failed to start" line the shipped 26.11.9 core printed for a
    // config with that defect, so these pin the fragments CoreStartupDiagnosis keys on.
    public class CoreStartupDiagnosisTests
    {
        private const string Prefix = "Failed to start: main: failed to load config files: [xray_config.json] > ";

        [Fact]
        public void Parse_PlaintextVless_NamesTheOutbound()
        {
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build outbound config with tag out-1 > infra/conf: vless without TLS "
                + "or other encryption is prohibited unless the server address is a private IP or domain");

            Assert.Equal(CoreStartupProblem.PlaintextProxy, d.Problem);
            Assert.Equal("out-1", d.OutboundTag);
            Assert.Null(d.Detail);
        }

        [Fact]
        public void Parse_PlaintextTrojan_IsTheSameProblem()
        {
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build outbound config with tag proxy > infra/conf: trojan without TLS "
                + "is prohibited unless the server address is a private IP or domain");

            Assert.Equal(CoreStartupProblem.PlaintextProxy, d.Problem);
            Assert.Equal("proxy", d.OutboundTag);
        }

        [Fact]
        public void Parse_UnknownCipher_CarriesTheMethod()
        {
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build outbound config with tag out-2 > infra/conf: failed to build "
                + "outbound handler for protocol shadowsocks > infra/conf: unknown cipher method: none");

            Assert.Equal(CoreStartupProblem.UnknownCipher, d.Problem);
            Assert.Equal("out-2", d.OutboundTag);
            Assert.Equal("none", d.Detail);
        }

        [Fact]
        public void Parse_UnknownCipher_EmptyMethod_HasEmptyDetail()
        {
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build outbound config with tag proxy > infra/conf: failed to build "
                + "outbound handler for protocol shadowsocks > infra/conf: unknown cipher method: ");

            Assert.Equal(CoreStartupProblem.UnknownCipher, d.Problem);
            Assert.Equal(string.Empty, d.Detail);
        }

        [Theory]
        [InlineData(
            "infra/conf: failed to build routing configuration > infra/conf: invalid field rule > common/geodata: "
            + "illegal domain rule: geosite:gfw > common/geodata: failed to check code GFW from geosite.dat > EOF",
            "geosite:gfw")]
        [InlineData(
            "infra/conf: failed to build routing configuration > infra/conf: invalid field rule > common/geodata: "
            + "illegal ip rule: geoip:us > common/geodata: failed to check code US from geoip.dat > EOF",
            "geoip:us")]
        [InlineData(
            "infra/conf: failed to build DNS configuration > infra/conf: failed to build nameserver > common/geodata: "
            + "illegal domain rule: geosite:apple-cn > common/geodata: failed to check code APPLE-CN from geosite.dat > EOF",
            "geosite:apple-cn")]
        public void Parse_MissingGeoCategory_CarriesTheRule(string chain, string expectedRule)
        {
            var d = CoreStartupDiagnosis.Parse(Prefix + chain);

            Assert.Equal(CoreStartupProblem.MissingGeoCategory, d.Problem);
            Assert.Null(d.OutboundTag);
            Assert.Equal(expectedRule, d.Detail);
        }

        [Fact]
        public void Parse_MissingGeoFile_IsNotAMissingCategory()
        {
            // Same "illegal domain rule" wrapper, different cause: the .dat itself is absent.
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build routing configuration > infra/conf: invalid field rule > common/geodata: "
                + "illegal domain rule: ext:nofile.dat:x > common/geodata: failed to open nofile.dat > GetFileAttributesEx");

            Assert.Equal(CoreStartupProblem.Unknown, d.Problem);
        }

        [Fact]
        public void Parse_UntaggedOutbound_HasNoTag()
        {
            // The core prints the empty tag as two spaces before the separator.
            var d = CoreStartupDiagnosis.Parse(Prefix
                + "infra/conf: failed to build outbound config with tag  > infra/conf: trojan without TLS "
                + "is prohibited unless the server address is a private IP or domain");

            Assert.Equal(CoreStartupProblem.PlaintextProxy, d.Problem);
            Assert.Null(d.OutboundTag);
        }

        [Fact]
        public void Parse_UnrecognizedFailure_StillReportsTheOutbound()
        {
            var d = CoreStartupDiagnosis.Parse(
                "2026/11/15 10:02:53 [Warning] common/errors: The feature VMess is deprecated\r\n" + Prefix
                + "infra/conf: failed to build outbound config with tag out-7 > infra/conf: something new\r\n");

            Assert.Equal(CoreStartupProblem.Unknown, d.Problem);
            Assert.Equal("out-7", d.OutboundTag);
            Assert.Null(d.Detail);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("listen tcp 127.0.0.1:10808: bind: Only one usage of each socket address")]
        public void Parse_NoKnownCause_IsUnknown(string? log)
        {
            var d = CoreStartupDiagnosis.Parse(log);

            Assert.Equal(CoreStartupProblem.Unknown, d.Problem);
            Assert.Null(d.OutboundTag);
        }
    }
}
