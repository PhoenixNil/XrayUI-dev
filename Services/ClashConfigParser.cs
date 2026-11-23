using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using XrayUI.Models;
using YamlDotNet.RepresentationModel;

namespace XrayUI.Services
{
    /// <summary>
    /// Parses the <c>proxies:</c> section of a Clash / Clash.Meta YAML config into
    /// <see cref="ServerEntry"/> instances. Field conventions mirror <see cref="NodeLinkParser"/>
    /// so an imported node is indistinguishable from a link-imported one downstream
    /// (<see cref="XrayConfigBuilder"/> etc.).
    ///
    /// Routing rules and proxy-groups are ignored by design — this is node-only import.
    /// Only protocols the Xray core supports are mapped (ss / vmess / vless / trojan /
    /// hysteria2 / socks5 / wireguard); everything else (tuic, anytls, hysteria v1, ssr, snell, …)
    /// is counted as skipped, and so is http, which import deliberately leaves out.
    ///
    /// AOT-safe: uses YamlDotNet's reflection-free <see cref="YamlStream"/> DOM, never the
    /// reflection-based <c>Deserializer</c>.
    /// </summary>
    public static class ClashConfigParser
    {
        /// <summary>
        /// Parses <paramref name="yamlText"/>. Throws on invalid YAML (the caller surfaces it
        /// as an import error). A valid document with no <c>proxies:</c> sequence yields an
        /// empty result rather than throwing.
        /// </summary>
        public static ClashParseResult Parse(string yamlText)
        {
            var nodes = new List<ServerEntry>();
            int skipped = 0;

            var stream = new YamlStream();
            stream.Load(new StringReader(yamlText));

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root ||
                Child(root, "proxies") is not YamlSequenceNode proxies)
                return new ClashParseResult(nodes, 0);

            foreach (var item in proxies.Children)
            {
                if (item is not YamlMappingNode p)
                    continue;

                var entry = MapProxy(p);
                if (entry is null)
                    skipped++;
                else
                    nodes.Add(entry);
            }

            return new ClashParseResult(nodes, skipped);
        }

        // Returns null for unsupported protocols and for entries missing a usable host/port
        // (so malformed rows don't pollute the list). Both count as "skipped".
        private static ServerEntry? MapProxy(YamlMappingNode p)
        {
            ServerEntry? entry = Str(p, "type").ToLowerInvariant() switch
            {
                "ss"                => MapSs(p),
                "vmess"             => MapVmess(p),
                "vless"             => MapVless(p),
                "trojan"            => MapTrojan(p),
                "hysteria2"         => MapHysteria2(p),
                "socks5" or "socks" => MapSocks5(p),
                "wireguard"         => MapWireguard(p),
                _                   => null,
            };

            if (entry is null || string.IsNullOrWhiteSpace(entry.Host) || entry.Port is <= 0 or > 65535)
                return null;

            return entry;
        }

        private static ServerEntry? MapSocks5(YamlMappingNode p)
        {
            // XrayConfigBuilder emits a plaintext TCP outbound for socks, so only import entries
            // that are certainly plaintext: a dropped TLS setting would send the credentials in the
            // clear. Skip when tls is anything but absent/false, and when a YAML merge key (not
            // resolved here) could be inheriting it.
            if (Child(p, "<<") is not null
                || (Child(p, "tls") is { } tls && !IsFalse(tls)))
                return null;

            return new ServerEntry
            {
                Name       = Str(p, "name"),
                Protocol   = "socks",
                Host       = Str(p, "server"),
                Port       = Int(p, "port"),
                Username   = Str(p, "username"),
                Password   = Str(p, "password"),
                Network    = "tcp",
                Security   = "none",
                Encryption = string.Empty,
            };
        }

        private static ServerEntry? MapSs(YamlMappingNode p)
        {
            if (Child(p, "plugin") is not null || Child(p, "plugin-opts") is not null)
                return null;

            return new ServerEntry
            {
                Name       = Str(p, "name"),
                Protocol   = "ss",
                Host       = Str(p, "server"),
                Port       = Int(p, "port"),
                Encryption = Str(p, "cipher"),
                Password   = Str(p, "password"),
                Network    = "tcp",
            };
        }

        private static ServerEntry? MapVmess(YamlMappingNode p)
        {
            if (Transport(p) is not { } t) return null;
            bool tls = Bool(p, "tls");
            return new ServerEntry
            {
                Name          = Str(p, "name"),
                Protocol      = "vmess",
                Host          = Str(p, "server"),
                Port          = Int(p, "port"),
                Uuid          = Str(p, "uuid"),
                AlterId       = Int(p, "alterId"),
                Network       = t.network,
                Path          = t.path,
                WsHost        = t.wsHost,
                Security      = tls ? "tls" : "none",
                Sni           = Str(p, "servername", Str(p, "sni")),
                Fingerprint   = Str(p, "client-fingerprint"),
                AllowInsecure = Bool(p, "skip-cert-verify"),
                Encryption    = tls ? "TLS" : "None",
            };
        }

        private static ServerEntry? MapVless(YamlMappingNode p)
        {
            if (Transport(p) is not { } t) return null;
            var reality = Map(p, "reality-opts");
            bool tls = Bool(p, "tls");
            string security = reality is not null ? "reality" : tls ? "tls" : "none";
            return new ServerEntry
            {
                Name          = Str(p, "name"),
                Protocol      = "vless",
                Host          = Str(p, "server"),
                Port          = Int(p, "port"),
                Uuid          = Str(p, "uuid"),
                Network       = t.network,
                Security      = security,
                Sni           = Str(p, "servername", Str(p, "sni")),
                Fingerprint   = Str(p, "client-fingerprint"),
                AllowInsecure = Bool(p, "skip-cert-verify"),
                PublicKey     = reality is null ? string.Empty : Str(reality, "public-key"),
                ShortId       = reality is null ? string.Empty : Str(reality, "short-id"),
                SpiderX       = reality is null ? string.Empty : Str(reality, "spider-x"),
                Path          = t.path,
                WsHost        = t.wsHost,
                Flow          = Str(p, "flow"),
                Encryption    = security == "reality" ? "Reality"
                              : security == "tls"     ? "TLS"
                                                      : "None",
            };
        }

        private static ServerEntry? MapTrojan(YamlMappingNode p)
        {
            if (Transport(p) is not { } t) return null;
            return new ServerEntry
            {
                Name          = Str(p, "name"),
                Protocol      = "trojan",
                Host          = Str(p, "server"),
                Port          = Int(p, "port"),
                Password      = Str(p, "password"),
                Network       = t.network,
                Security      = "tls",
                Sni           = Str(p, "sni", Str(p, "servername")),
                Fingerprint   = Str(p, "client-fingerprint"),
                AllowInsecure = Bool(p, "skip-cert-verify"),
                Path          = t.path,
                WsHost        = t.wsHost,
                Encryption    = "TLS",
            };
        }

        private static ServerEntry MapHysteria2(YamlMappingNode p) => new()
        {
            Name          = Str(p, "name"),
            Protocol      = "hysteria2",
            Host          = Str(p, "server"),
            Port          = Int(p, "port"),
            Password      = Str(p, "password"),
            Network       = "udp",
            Security      = "tls",
            Sni           = Str(p, "sni", Str(p, "servername")),
            AllowInsecure = Bool(p, "skip-cert-verify"),
            // Clash spells the pinned certificate digest `fingerprint` — unrelated to the
            // uTLS `client-fingerprint` that maps to ServerEntry.Fingerprint elsewhere.
            PinnedPeerCertSha256 = Str(p, "fingerprint"),
            Finalmask     = BuildHysteria2Finalmask(p),
            Encryption    = "TLS",
        };

        private static string BuildHysteria2Finalmask(YamlMappingNode p)
        {
            var finalmask = FinalmaskJson.NormalizeForStorage(Str(p, "fm", Str(p, "finalmask")));

            if (string.Equals(Str(p, "obfs"), "salamander", StringComparison.OrdinalIgnoreCase))
            {
                var obfsPassword = Str(p, "obfs-password", Str(p, "obfs_password", Str(p, "obfsPassword")));
                if (!string.IsNullOrWhiteSpace(obfsPassword))
                    finalmask = FinalmaskJson.AddHysteria2SalamanderMask(finalmask, obfsPassword);
            }

            // Port hopping. The v2rayN share-link spelling ("mport") is deliberately not accepted
            // here — that vocabulary belongs to NodeLinkParser, no Clash config emits it.
            var ports = Str(p, "ports");
            if (!string.IsNullOrWhiteSpace(ports))
                finalmask = FinalmaskJson.AddHysteria2UdpHop(finalmask, ports, Str(p, "hop-interval"));

            return finalmask;
        }

        // WireGuard: single-peer form (top-level public-key / server / port). Returns null when a
        // required field is missing so the newer multi-peer `peers:` form and malformed rows skip.
        private static ServerEntry? MapWireguard(YamlMappingNode p)
        {
            var privateKey = Str(p, "private-key");
            var publicKey  = Str(p, "public-key");
            var server     = Str(p, "server");
            if (string.IsNullOrWhiteSpace(privateKey)
                || string.IsNullOrWhiteSpace(publicKey)
                || string.IsNullOrWhiteSpace(server))
                return null;

            return new ServerEntry
            {
                Name           = Str(p, "name"),
                Protocol       = "wireguard",
                Host           = server,
                Port           = Int(p, "port"),
                WgPrivateKey   = privateKey,
                WgPublicKey    = publicKey,
                WgPreSharedKey = Str(p, "pre-shared-key", Str(p, "preshared-key")),
                WgLocalAddress = WireguardLocalAddress(p),
                WgReserved     = Reserved(p),
                WgMtu          = Int(p, "mtu"),
                Encryption     = "WireGuard",
            };
        }

        // Clash gives local tunnel IPs as bare `ip` / `ipv6`; xray's settings.address wants CIDRs.
        private static string WireguardLocalAddress(YamlMappingNode p)
        {
            var parts = new List<string>(2);
            AddCidr(parts, Str(p, "ip"), "/32");
            AddCidr(parts, Str(p, "ipv6"), "/128");
            return string.Join(",", parts);
        }

        private static void AddCidr(List<string> into, string address, string suffix)
        {
            if (string.IsNullOrWhiteSpace(address))
                return;
            into.Add(address.Contains('/') ? address : address + suffix);
        }

        // reserved is usually a 3-int sequence ([a,b,c]); some configs use a scalar (base64 / "a,b,c").
        private static string Reserved(YamlMappingNode p) => Child(p, "reserved") switch
        {
            YamlSequenceNode seq => string.Join(",", ScalarValues(seq)),
            YamlScalarNode { Value: { } v } => v,
            _ => string.Empty,
        };

        private static IEnumerable<string> ScalarValues(YamlSequenceNode seq)
        {
            foreach (var item in seq.Children)
                if (item is YamlScalarNode { Value: { } v })
                    yield return v;
        }

        // Resolves the transport into the (network, path, wsHost) triple XrayConfigBuilder
        // consumes. Returns null for network types XrayConfigBuilder can't build streamSettings
        // for (h2/http/kcp/quic/…) so the caller skips the node rather than importing an
        // unconnectable one. Note grpc's service name lives in grpc-opts (not ws-opts) and, like
        // ws's path, is carried on ServerEntry.Path (see XrayConfigBuilder.BuildStreamSettings).
        private static (string network, string path, string wsHost)? Transport(YamlMappingNode p)
        {
            switch (Str(p, "network", "tcp").ToLowerInvariant())
            {
                case "tcp":
                    return ("tcp", string.Empty, string.Empty);
                case "ws":
                {
                    var (wsPath, wsHost) = WsOpts(p);
                    return ("ws", wsPath, wsHost);
                }
                case "grpc":
                {
                    var grpc = Map(p, "grpc-opts");
                    var serviceName = grpc is null ? string.Empty : Str(grpc, "grpc-service-name");
                    return ("grpc", serviceName, string.Empty);
                }
                default:
                    return null;
            }
        }

        // ws-opts: { path, headers: { Host } }
        private static (string path, string wsHost) WsOpts(YamlMappingNode p)
        {
            var ws = Map(p, "ws-opts");
            if (ws is null)
                return (string.Empty, string.Empty);

            var headers = Map(ws, "headers");
            var host = headers is null ? string.Empty : Str(headers, "Host");
            return (Str(ws, "path"), host);
        }

        // ── YAML DOM accessors (case-insensitive keys, no reflection) ──────────

        private static YamlNode? Child(YamlMappingNode map, string key)
        {
            foreach (var kv in map.Children)
                if (kv.Key is YamlScalarNode s &&
                    string.Equals(s.Value, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        private static string Str(YamlMappingNode map, string key, string def = "")
            => Child(map, key) is YamlScalarNode { Value: { } v } ? v : def;

        private static int Int(YamlMappingNode map, string key, int def = 0)
            => Child(map, key) is YamlScalarNode s && int.TryParse(s.Value, out var n) ? n : def;

        private static bool Bool(YamlMappingNode map, string key, bool def = false)
            => Child(map, key) is YamlScalarNode { Value: { } v } && TryParseBool(v, out var b) ? b : def;

        private static bool IsFalse(YamlNode node)
            => node is YamlScalarNode { Value: { } v } && TryParseBool(v, out var b) && !b;

        // Mirrors mihomo's bool decoding: integers are true when non-zero, strings follow
        // Go's strconv.ParseBool (true/t/false/f).
        private static bool TryParseBool(string v, out bool value)
        {
            if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                value = n != 0;
                return true;
            }

            value = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("t", StringComparison.OrdinalIgnoreCase);
            return value || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("f", StringComparison.OrdinalIgnoreCase);
        }

        private static YamlMappingNode? Map(YamlMappingNode map, string key)
            => Child(map, key) as YamlMappingNode;
    }

    public sealed record ClashParseResult(List<ServerEntry> Nodes, int Skipped);
}
