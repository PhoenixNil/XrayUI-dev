using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayUI.Services
{
    public enum ConfigProfileError
    {
        None,
        /// <summary>Nothing to parse. A profile has to describe a whole config.</summary>
        Empty,
        InvalidJson,
        RootMustBeObject,
        /// <summary>outbounds come from the selected node, so a profile may not declare them.</summary>
        OutboundsNotAllowed,
        /// <summary>No inbounds array, or an empty one — xray would accept no traffic at all.</summary>
        InboundsMissing,
        /// <summary>TUN slot with no protocol:"tun" inbound.</summary>
        TunInboundMissing,
        /// <summary>System-proxy slot carrying a TUN inbound. TUN is selected by the mode toggle,
        /// which is what drives elevation and route cleanup — a profile may not smuggle it in.</summary>
        TunInboundNotAllowed,
        /// <summary>TUN inbound whose settings.name is not the adapter alias TunService cleans up.</summary>
        TunInterfaceNameMismatch,
    }

    [Flags]
    public enum ConfigProfileWarning
    {
        None = 0,
        /// <summary>No socks/http inbound with a plain integer port, so the system proxy has
        /// nothing to point at and will be skipped.</summary>
        NoSystemProxyInbound = 1,
        /// <summary>routing references an outbound tag that is neither injected nor a balancer.</summary>
        UnknownOutboundTag = 2,
        /// <summary>TUN inbound without autoSystemRoutingTable — xray will not hijack the
        /// default route, so traffic keeps leaving over the physical adapter.</summary>
        NoAutoSystemRouting = 4,
    }

    public readonly record struct ConfigProfileResult(
        JsonObject? Config,
        ConfigProfileError Error,
        ConfigProfileWarning Warnings = ConfigProfileWarning.None,
        string? Detail = null)
    {
        public bool IsValid => Error == ConfigProfileError.None && Config is not null;
    }

    /// <summary>
    /// Validates a hand-written config profile: a complete xray config minus outbounds, which
    /// XrayUI injects from the selected node.
    ///
    /// Errors block the save; warnings describe a config that will start but not do what the
    /// user probably meant. Both are returned as enums rather than messages so this file stays
    /// free of WinAppSDK — it is source-linked into XrayUI.Tests, and the localized text is
    /// assembled by the editor window.
    /// </summary>
    public static class ConfigProfileJson
    {
        private const string OutboundsKey = "outbounds";
        private const string InboundsKey = "inbounds";
        private const string RoutingKey = "routing";

        /// <summary>Parse depth cap; also bounds <see cref="Materialize"/>'s recursion.</summary>
        private const int MaxDepth = 64;

        /// <summary>Outbound tags XrayUI injects, so a profile's routing may target them. Kept in
        /// agreement with XrayConfigBuilder.BuildOutbounds' profileMode branch, which emits them.
        /// chain-entry only exists for chained nodes, but naming it is not an error — the user
        /// may well be running a chain.</summary>
        public static readonly string[] InjectedOutboundTags =
        [
            XrayConfigConstants.ProxyOutboundTag,
            XrayConfigConstants.DirectOutboundTag,
            XrayConfigConstants.BlockOutboundTag,
            XrayConfigConstants.DnsOutboundTag,
            XrayConfigConstants.ChainEntryOutboundTag,
        ];

        /// <summary>
        /// Forces every object in the tree to build its backing dictionary.
        /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> happily
        /// accepts duplicate members and defers the conflict to whenever that particular object is
        /// first indexed or enumerated — where it surfaces as an <see cref="ArgumentException"/>,
        /// not a <see cref="JsonException"/>. Left alone, the top level blew up on the first
        /// ContainsKey below (escaping this method's catch, and with it the editor's Save handler,
        /// as an unhandled UI-thread exception), while nested duplicates passed validation
        /// outright and only failed at config-build time — after the unusable profile had been
        /// saved. Doing it here puts both inside the try.
        /// </summary>
        private static void Materialize(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    // Enumerating is what triggers InitializeDictionary.
                    foreach (var (_, value) in obj) Materialize(value);
                    break;
                case JsonArray array:
                    foreach (var item in array) Materialize(item);
                    break;
            }
        }

        /// <param name="tunSlot">Which slot the profile is being saved into. TUN is decided by
        /// the mode toggle, so the slot — not the file's contents — says whether a TUN inbound
        /// is required or forbidden.</param>
        public static ConfigProfileResult Validate(string? json, bool tunSlot)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ConfigProfileResult(null, ConfigProfileError.Empty);

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(
                    json,
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = MaxDepth,
                    });

                Materialize(node);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return new ConfigProfileResult(null, ConfigProfileError.InvalidJson, Detail: ex.Message);
            }

            if (node is not JsonObject config)
                return new ConfigProfileResult(null, ConfigProfileError.RootMustBeObject);

            if (config.ContainsKey(OutboundsKey))
                return new ConfigProfileResult(null, ConfigProfileError.OutboundsNotAllowed);

            if (config[InboundsKey] is not JsonArray inbounds || inbounds.Count == 0)
                return new ConfigProfileResult(null, ConfigProfileError.InboundsMissing);

            var tun = inbounds.OfType<JsonObject>()
                .FirstOrDefault(i => AsString(i["protocol"]) == "tun");

            if (tunSlot && tun is null)
                return new ConfigProfileResult(null, ConfigProfileError.TunInboundMissing);

            if (!tunSlot && tun is not null)
                return new ConfigProfileResult(null, ConfigProfileError.TunInboundNotAllowed);

            var warnings = ConfigProfileWarning.None;

            if (tun is not null)
            {
                var tunSettings = tun["settings"] as JsonObject;

                // TunService hardcodes this alias in its netsh route/DNS cleanup commands, so a
                // different name leaves stale routes behind after a disconnect with no error.
                if (AsString(tunSettings?["name"]) != XrayConfigConstants.TunInterfaceName)
                    return new ConfigProfileResult(null, ConfigProfileError.TunInterfaceNameMismatch);

                if (tunSettings?["autoSystemRoutingTable"] is not JsonArray routes || routes.Count == 0)
                    warnings |= ConfigProfileWarning.NoAutoSystemRouting;
            }
            else if (FindSystemProxyPort(inbounds) is null)
            {
                // Only meaningful for the system-proxy slot: the TUN path never sets the
                // WinInet proxy, so a TUN profile without a socks inbound is a normal setup.
                warnings |= ConfigProfileWarning.NoSystemProxyInbound;
            }

            if (HasUnknownOutboundTag(config))
                warnings |= ConfigProfileWarning.UnknownOutboundTag;

            return new ConfigProfileResult(config, ConfigProfileError.None, warnings);
        }

        /// <summary>
        /// The port the system proxy should point at: the inbound tagged <c>mixed-in</c> if the
        /// profile kept that name, otherwise the first socks/http inbound with a plain integer
        /// port. Ranges and string ports are skipped — WinInet needs a single number.
        /// </summary>
        public static int? FindSystemProxyPort(JsonArray inbounds)
        {
            var tagged = inbounds.OfType<JsonObject>()
                .FirstOrDefault(i => AsString(i["tag"]) == XrayConfigConstants.MixedInboundTag);

            if (AsPort(tagged?["port"]) is { } taggedPort) return taggedPort;

            foreach (var inbound in inbounds.OfType<JsonObject>())
            {
                if (AsString(inbound["protocol"]) is not ("socks" or "http" or "mixed")) continue;
                if (AsPort(inbound["port"]) is { } port) return port;
            }

            return null;
        }

        private static bool HasUnknownOutboundTag(JsonObject config)
        {
            if (config[RoutingKey] is not JsonObject routing) return false;
            if (routing["rules"] is not JsonArray rules) return false;

            var known = new HashSet<string>(InjectedOutboundTags, StringComparer.Ordinal);

            // A rule may target a balancer the profile declares itself instead of an outbound.
            if (routing["balancers"] is JsonArray balancers)
            {
                foreach (var balancer in balancers.OfType<JsonObject>())
                {
                    if (AsString(balancer["tag"]) is { } tag) known.Add(tag);
                }
            }

            return rules.OfType<JsonObject>()
                .Select(rule => AsString(rule["outboundTag"]))
                .Any(tag => tag is not null && !known.Contains(tag));
        }

        private static string? AsString(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static int? AsPort(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<int>(out var port) ? port : null;
    }
}
