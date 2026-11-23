using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace XrayUI.Services
{
    /// <summary>
    /// Predicts the core's refusal of a VLESS or Trojan outbound that has no transport security
    /// and points at a public address (Xray-core 26.7.11+, <c>validateOutboundTransportSecurity</c>
    /// in infra/conf/xray.go). The core refuses the whole config file over one such outbound, and
    /// large shared subscriptions carry dozens of them, so the latency probe leaves them out of
    /// its config instead of finding them one restart at a time.
    ///
    /// Reads the outbound JSON as built, the same fields the core reads, so the builder's
    /// ServerEntry-to-JSON mapping cannot drift from it. One-sided on purpose: true only when the
    /// core is certain to refuse. Input this cannot read exactly the way the core does answers
    /// false, and the core stays the judge (<see cref="CoreStartupDiagnosis"/>). A miss costs one
    /// core restart; a wrong true would hide a usable node.
    /// </summary>
    public static class PlaintextProxyRule
    {
        // The core's private-domain list (common/geodata/consts.go): each entry matches itself
        // and its subdomains. The list's last entry, dotless names, is IsDotlessName below.
        private static readonly string[] PrivateDomainSuffixes =
        [
            "lan", "localdomain", "example", "invalid", "localhost",
            "test", "local", "home.arpa", "internal",
        ];

        public static bool IsRefused(JsonObject outbound)
        {
            var protocol = Text(outbound["protocol"]);
            bool isVless = protocol == "vless";
            if (!isVless && protocol != "trojan")
                return false;

            // tls and reality are the only values that give the stream a security type.
            if (outbound["streamSettings"] is JsonObject stream && !IsNoSecurity(stream["security"]))
                return false;

            if (outbound["settings"] is not JsonObject settings)
                return false;

            // Only the shapes XrayConfigBuilder emits are read: VLESS as one vnext with one user,
            // Trojan in the flat form. Any other shape answers false and is left to the core
            // (a flat "address" would override vnext there).
            string? address;
            if (isVless)
            {
                if (settings["address"] is not null
                    || OnlyObject(settings["vnext"]) is not { } vnext
                    || OnlyObject(vnext["users"]) is not { } user
                    || !IsNoEncryption(user["encryption"]))
                    return false;
                address = Text(vnext["address"]);
            }
            else
            {
                address = Text(settings["address"]);
            }

            return address is not null && IsPublicAddress(address);
        }

        /// <summary>The core reads "" and "none", in any case, as no security; absent reads as "".</summary>
        private static bool IsNoSecurity(JsonNode? node) =>
            node is null
            || Text(node) is { } security
                && (security.Length == 0 || security.Equals("none", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The core skips the check when a VLESS user has encryption other than "" or "none",
        /// compared exactly; an absent key unmarshals to "".
        /// </summary>
        private static bool IsNoEncryption(JsonNode? node) =>
            node is null || Text(node) is "" or "none";

        /// <summary>
        /// The core's <c>requiresTransportSecurity</c>, for addresses read exactly as the core
        /// parses them (common/net ParseAddress). Anything else answers false.
        /// </summary>
        private static bool IsPublicAddress(string address)
        {
            bool bracketed = address.Length >= 2 && address[0] == '[' && address[^1] == ']';
            if (bracketed)
                address = address[1..^1];

            // Empty, whitespace (the core trims some), control or non-ASCII characters (case
            // mapping): not worth mirroring exactly.
            if (address.Length == 0 || address.AsSpan().ContainsAnyExceptInRange('!', '~'))
                return false;

            if (address.Contains(':'))
            {
                // A colon selects the IPv6 parser. It rejects env: references, which the core resolves.
                // Go rejects zone IDs and reads bracketed IPv6 endpoints with ports as public domains.
                if (address.Contains('%') || !IPAddress.TryParse(address, out var ip))
                    return false;
                if (ip.IsIPv4MappedToIPv6)
                    return !IsPrivateIPv4(ip.MapToIPv4().GetAddressBytes());
                // Go's IsPrivate (fc00::/7), IsLinkLocalUnicast (fe80::/10) and IsLoopback (::1).
                return !(ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal || IPAddress.IsLoopback(ip));
            }

            if (bracketed)
                return false; // "[name]" is an odd input; leave it to the core

            // Go takes only the canonical dotted quad, which is exactly what .NET prints back.
            if (IPAddress.TryParse(address, out var v4) && v4.ToString() == address)
                return !IsPrivateIPv4(v4.GetAddressBytes());

            // "1.2.3", "01.2.3.4": .NET reads these as IPv4, Go as a domain. Digits and dots
            // only, and not a canonical IPv4 address: let the core decide.
            if (!address.AsSpan().ContainsAnyExcept("0123456789."))
                return false;

            var domain = address.ToLowerInvariant();
            if (domain.EndsWith('.'))
                domain = domain[..^1];
            return !IsPrivateDomain(domain);
        }

        // Go's IP.IsLoopback, IsPrivate and IsLinkLocalUnicast, the three the core checks.
        private static bool IsPrivateIPv4(byte[] b) =>
            b[0] == 127                              // loopback
            || b[0] == 10                            // 10.0.0.0/8
            || (b[0] == 172 && (b[1] & 0xF0) == 16)  // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)          // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254);         // link-local

        private static bool IsPrivateDomain(string domain) =>
            IsDotlessName(domain)
            || PrivateDomainSuffixes.Any(suffix =>
                domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal));

        /// <summary>The core's <c>^[a-z]([a-z0-9-]{0,61}[a-z0-9])?$</c>.</summary>
        private static bool IsDotlessName(string domain) =>
            domain.Length is > 0 and <= 63
            && char.IsAsciiLetterLower(domain[0])
            && domain[^1] != '-'
            && !domain.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-");

        private static JsonObject? OnlyObject(JsonNode? node) =>
            node is JsonArray { Count: 1 } array ? array[0] as JsonObject : null;

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
