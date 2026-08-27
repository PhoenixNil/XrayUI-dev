using System.Text.Json.Nodes;
using XrayUI.Services;

namespace XrayUI.Tests;

public class ConfigProfileJsonTests
{
    private const bool TunSlot = true;
    private const bool ProxySlot = false;

    /// <summary>A minimal profile that passes every check for the system-proxy slot.</summary>
    private const string ValidProxyProfile = """
        {
          "log": { "loglevel": "warning" },
          "inbounds": [
            {
              "tag": "mixed-in",
              "protocol": "socks",
              "listen": "127.0.0.1",
              "port": 16890,
              "settings": { "auth": "noauth", "udp": true }
            }
          ],
          "routing": {
            "domainStrategy": "IPIfNonMatch",
            "rules": [ { "type": "field", "port": "0-65535", "outboundTag": "proxy" } ]
          }
        }
        """;

    /// <summary>A minimal profile that passes every check for the TUN slot.</summary>
    private const string ValidTunProfile = """
        {
          "inbounds": [
            {
              "tag": "tun-in",
              "protocol": "tun",
              "settings": {
                "name": "xray-tun",
                "mtu": 1500,
                "gateway": [ "172.18.0.1/30" ],
                "autoSystemRoutingTable": [ "0.0.0.0/0" ]
              }
            },
            { "tag": "mixed-in", "protocol": "socks", "port": 16890 }
          ]
        }
        """;

    // ── JSON layer ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_Blank_IsRejected(string? json)
    {
        var result = ConfigProfileJson.Validate(json, ProxySlot);

        Assert.Equal(ConfigProfileError.Empty, result.Error);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_InvalidJson_IsRejectedWithParserDetail()
    {
        var result = ConfigProfileJson.Validate("{ \"inbounds\": [ ", ProxySlot);

        Assert.Equal(ConfigProfileError.InvalidJson, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Validate_NonObjectRoot_IsRejected(string json)
    {
        Assert.Equal(
            ConfigProfileError.RootMustBeObject,
            ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    [Fact]
    public void Validate_TrailingComma_IsRejected()
    {
        var json = """{ "inbounds": [ { "protocol": "socks", "port": 1080 } ], }""";

        Assert.Equal(ConfigProfileError.InvalidJson, ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    /// <summary>
    /// JsonNode.Parse defers duplicate members to first use, where they surface as
    /// ArgumentException rather than JsonException. Both levels have to be caught here, not
    /// thrown at the caller.
    /// </summary>
    [Fact]
    public void Validate_DuplicateTopLevelKeys_IsRejectedInsteadOfThrowing()
    {
        var json = """{ "inbounds": [], "inbounds": [] }""";

        Assert.Equal(ConfigProfileError.InvalidJson, ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    [Fact]
    public void Validate_DuplicateNestedKeys_IsRejectedInsteadOfThrowing()
    {
        var json = """{ "inbounds": [ { "port": 1, "port": 2 } ] }""";

        Assert.Equal(ConfigProfileError.InvalidJson, ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    // ── Structural contract ───────────────────────────────────────────────

    [Fact]
    public void Validate_Outbounds_IsRejected()
    {
        var json = """{ "inbounds": [ { "protocol": "socks", "port": 1080 } ], "outbounds": [] }""";

        Assert.Equal(
            ConfigProfileError.OutboundsNotAllowed,
            ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    [Theory]
    [InlineData("""{ "log": {} }""")]
    [InlineData("""{ "inbounds": [] }""")]
    [InlineData("""{ "inbounds": {} }""")]
    public void Validate_MissingOrEmptyInbounds_IsRejected(string json)
    {
        Assert.Equal(
            ConfigProfileError.InboundsMissing,
            ConfigProfileJson.Validate(json, ProxySlot).Error);
    }

    [Fact]
    public void Validate_TunSlotWithoutTunInbound_IsRejected()
    {
        Assert.Equal(
            ConfigProfileError.TunInboundMissing,
            ConfigProfileJson.Validate(ValidProxyProfile, TunSlot).Error);
    }

    [Fact]
    public void Validate_ProxySlotWithTunInbound_IsRejected()
    {
        Assert.Equal(
            ConfigProfileError.TunInboundNotAllowed,
            ConfigProfileJson.Validate(ValidTunProfile, ProxySlot).Error);
    }

    /// <summary>TunService deletes the adapter's routes by this exact alias.</summary>
    [Theory]
    [InlineData("tun0")]
    [InlineData("")]
    [InlineData("XRAY-TUN")]
    public void Validate_TunInterfaceNameMismatch_IsRejected(string name)
    {
        var json = ValidTunProfile.Replace("\"name\": \"xray-tun\"", $"\"name\": \"{name}\"");

        Assert.Equal(
            ConfigProfileError.TunInterfaceNameMismatch,
            ConfigProfileJson.Validate(json, TunSlot).Error);
    }

    [Fact]
    public void Validate_TunInboundWithoutSettings_IsRejectedAsNameMismatch()
    {
        var json = """{ "inbounds": [ { "protocol": "tun" } ] }""";

        Assert.Equal(
            ConfigProfileError.TunInterfaceNameMismatch,
            ConfigProfileJson.Validate(json, TunSlot).Error);
    }

    // ── Happy paths ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidProxyProfile_HasNoWarnings()
    {
        var result = ConfigProfileJson.Validate(ValidProxyProfile, ProxySlot);

        Assert.True(result.IsValid);
        Assert.Equal(ConfigProfileWarning.None, result.Warnings);
        Assert.NotNull(result.Config);
    }

    [Fact]
    public void Validate_ValidTunProfile_HasNoWarnings()
    {
        var result = ConfigProfileJson.Validate(ValidTunProfile, TunSlot);

        Assert.True(result.IsValid);
        Assert.Equal(ConfigProfileWarning.None, result.Warnings);
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("direct")]
    [InlineData("block")]
    [InlineData("dns-out")]
    [InlineData("chain-entry")]
    public void Validate_InjectedOutboundTags_AreAccepted(string tag)
    {
        var json = ValidProxyProfile.Replace("\"outboundTag\": \"proxy\"", $"\"outboundTag\": \"{tag}\"");

        Assert.Equal(ConfigProfileWarning.None, ConfigProfileJson.Validate(json, ProxySlot).Warnings);
    }

    // ── Warnings ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ProxyProfileWithoutSocksInbound_WarnsSystemProxyCannotBeSet()
    {
        var json = """{ "inbounds": [ { "protocol": "dokodemo-door", "port": 1080 } ] }""";

        var result = ConfigProfileJson.Validate(json, ProxySlot);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings.HasFlag(ConfigProfileWarning.NoSystemProxyInbound));
    }

    /// <summary>WinInet needs one number, so a port range cannot back the system proxy.</summary>
    [Fact]
    public void Validate_PortRange_WarnsSystemProxyCannotBeSet()
    {
        var json = """{ "inbounds": [ { "protocol": "socks", "port": "1080-1090" } ] }""";

        Assert.True(ConfigProfileJson.Validate(json, ProxySlot)
            .Warnings.HasFlag(ConfigProfileWarning.NoSystemProxyInbound));
    }

    /// <summary>The TUN path never writes the WinInet registry, so a TUN profile with no socks
    /// inbound is a normal setup rather than something to warn about.</summary>
    [Fact]
    public void Validate_TunProfileWithoutSocksInbound_DoesNotWarn()
    {
        var json = """
            {
              "inbounds": [
                {
                  "protocol": "tun",
                  "settings": { "name": "xray-tun", "autoSystemRoutingTable": [ "0.0.0.0/0" ] }
                }
              ]
            }
            """;

        Assert.Equal(ConfigProfileWarning.None, ConfigProfileJson.Validate(json, TunSlot).Warnings);
    }

    [Fact]
    public void Validate_TunWithoutAutoSystemRoutingTable_Warns()
    {
        var json = """
            {
              "inbounds": [ { "protocol": "tun", "settings": { "name": "xray-tun" } } ]
            }
            """;

        var result = ConfigProfileJson.Validate(json, TunSlot);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings.HasFlag(ConfigProfileWarning.NoAutoSystemRouting));
    }

    [Fact]
    public void Validate_UnknownOutboundTag_Warns()
    {
        var json = ValidProxyProfile.Replace("\"outboundTag\": \"proxy\"", "\"outboundTag\": \"my-proxy\"");

        var result = ConfigProfileJson.Validate(json, ProxySlot);

        Assert.True(result.IsValid);
        Assert.True(result.Warnings.HasFlag(ConfigProfileWarning.UnknownOutboundTag));
    }

    /// <summary>A rule may target a balancer the profile declares itself.</summary>
    [Fact]
    public void Validate_BalancerTag_IsNotUnknown()
    {
        var json = """
            {
              "inbounds": [ { "protocol": "socks", "port": 1080 } ],
              "routing": {
                "balancers": [ { "tag": "fastest", "selector": [ "proxy" ] } ],
                "rules": [ { "type": "field", "network": "tcp", "outboundTag": "fastest" } ]
              }
            }
            """;

        Assert.False(ConfigProfileJson.Validate(json, ProxySlot)
            .Warnings.HasFlag(ConfigProfileWarning.UnknownOutboundTag));
    }

    /// <summary>balancerTag rules carry no outboundTag at all and must not trip the check.</summary>
    [Fact]
    public void Validate_RuleWithoutOutboundTag_IsNotUnknown()
    {
        var json = """
            {
              "inbounds": [ { "protocol": "socks", "port": 1080 } ],
              "routing": { "rules": [ { "type": "field", "balancerTag": "fastest" } ] }
            }
            """;

        Assert.False(ConfigProfileJson.Validate(json, ProxySlot)
            .Warnings.HasFlag(ConfigProfileWarning.UnknownOutboundTag));
    }

    [Fact]
    public void Validate_MultipleWarnings_AreAllReported()
    {
        var json = """
            {
              "inbounds": [ { "protocol": "tun", "settings": { "name": "xray-tun" } } ],
              "routing": { "rules": [ { "type": "field", "outboundTag": "nope" } ] }
            }
            """;

        var result = ConfigProfileJson.Validate(json, TunSlot);

        Assert.True(result.Warnings.HasFlag(ConfigProfileWarning.NoAutoSystemRouting));
        Assert.True(result.Warnings.HasFlag(ConfigProfileWarning.UnknownOutboundTag));
    }

    // ── System proxy port resolution ──────────────────────────────────────

    [Fact]
    public void FindSystemProxyPort_PrefersTheMixedInTag()
    {
        var inbounds = JsonNode.Parse("""
            [
              { "protocol": "http", "port": 8080 },
              { "tag": "mixed-in", "protocol": "socks", "port": 16890 }
            ]
            """)!.AsArray();

        Assert.Equal(16890, ConfigProfileJson.FindSystemProxyPort(inbounds));
    }

    [Fact]
    public void FindSystemProxyPort_FallsBackToTheFirstSocksOrHttpInbound()
    {
        var inbounds = JsonNode.Parse("""
            [
              { "protocol": "tun", "port": 0 },
              { "protocol": "http", "port": 8080 }
            ]
            """)!.AsArray();

        Assert.Equal(8080, ConfigProfileJson.FindSystemProxyPort(inbounds));
    }

    [Fact]
    public void FindSystemProxyPort_SkipsATaggedInboundWithoutAnIntegerPort()
    {
        var inbounds = JsonNode.Parse("""
            [
              { "tag": "mixed-in", "protocol": "socks", "port": "1080-1090" },
              { "protocol": "socks", "port": 1081 }
            ]
            """)!.AsArray();

        Assert.Equal(1081, ConfigProfileJson.FindSystemProxyPort(inbounds));
    }

    [Fact]
    public void FindSystemProxyPort_ReturnsNullWhenNothingListens()
    {
        var inbounds = JsonNode.Parse("""[ { "protocol": "dokodemo-door", "port": 1080 } ]""")!.AsArray();

        Assert.Null(ConfigProfileJson.FindSystemProxyPort(inbounds));
    }
}
