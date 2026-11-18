using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// A built config plus the runtime facts the C# side needs from it.
    /// </summary>
    /// <param name="Json">The config text handed to xray.exe.</param>
    /// <param name="SystemProxyPort">The port SystemProxyService should advertise, read back out
    /// of the built config so a profile that moves the port cannot desync the two. Null when the
    /// config exposes no socks/http inbound on a plain integer port.</param>
    /// <param name="RouteTest">Where the running core answers route tests. Null only when a config
    /// profile declares its own api section.</param>
    public readonly record struct BuiltXrayConfig(string Json, int? SystemProxyPort, RouteTestEndpoint? RouteTest);

    /// <summary>
    /// Builds an xray-core JSON configuration string for the given server and app settings.
    /// Uses JsonObject/JsonArray so Native AOT does not need reflection-based serialization.
    /// </summary>
    public static class XrayConfigBuilder
    {
        private const string ProxyOutboundTag      = XrayConfigConstants.ProxyOutboundTag;
        private const string DirectOutboundTag     = XrayConfigConstants.DirectOutboundTag;
        private const string BlockOutboundTag      = XrayConfigConstants.BlockOutboundTag;
        private const string ChainEntryOutboundTag = XrayConfigConstants.ChainEntryOutboundTag;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        /// <param name="apiPort">Loopback port for the route-test API.</param>
        public static BuiltXrayConfig Build(
            ServerEntry server,
            AppSettings settings,
            int apiPort,
            IEnumerable<ServerEntry>? availableServers = null,
            string? profileJson = null)
        {
            var config = profileJson is null
                ? BuildGenerated(settings)
                : ParseProfileOrThrow(profileJson, settings.IsTunMode);

            // outbounds are never the profile's to write: they are the selected node. Injecting
            // them last means the same code path serves both, and a profile cannot desync the
            // config from the server list.
            var outbounds = BuildOutbounds(
                server, settings, availableServers,
                profileMode: profileJson is not null,
                forwardNonAddressDns: TunBlocksOutsideDns(config));
            config["outbounds"] = outbounds;

            var routeTest = AddRouteTestApi(config, outbounds, apiPort, settings.IsTunMode);

            return new BuiltXrayConfig(
                config.ToJsonString(JsonOpts),
                ResolveSystemProxyPort(config),
                routeTest);
        }

        /// <summary>
        /// Exposes RoutingService on loopback so the log window can ask the live router where a
        /// destination goes. api.listen opens the gRPC listener directly, with no inbound or
        /// routing rule of its own, so the rest of the config is untouched. Added here rather than
        /// in BuildGenerated so profile templates stay free of it; a profile that declares its own
        /// api section keeps it, and route tests are then unavailable.
        /// </summary>
        private static RouteTestEndpoint? AddRouteTestApi(
            JsonObject config, JsonArray outbounds, int port, bool tunMode)
        {
            if (config.ContainsKey("api")) return null;

            config["api"] = new JsonObject
            {
                ["tag"] = XrayConfigConstants.ApiTag,
                ["listen"] = $"127.0.0.1:{port}",
                ["services"] = CreateStringArray("RoutingService"),
            };

            // Both read back from the built config rather than assumed, like the system proxy
            // port: a profile may tag its inbounds anything, and xray's fallback for unmatched
            // traffic is whichever outbound happens to come first.
            var inboundTag = config["inbounds"] is JsonArray inbounds
                ? ConfigProfileJson.FindRouteTestInboundTag(inbounds, tunMode)
                : null;
            var defaultOutboundTag =
                outbounds.FirstOrDefault() is JsonObject first
                && first["tag"] is JsonValue tag
                && tag.TryGetValue<string>(out var text)
                    ? text
                    : string.Empty;

            return new RouteTestEndpoint(port, inboundTag, defaultOutboundTag);
        }

        /// <summary>
        /// Everything XrayUI generates itself. outbounds is an empty placeholder that Build
        /// overwrites: assigning to a key JsonObject already holds keeps its position, which is
        /// what preserves the historical log/dns/inbounds/outbounds/routing order in the file.
        /// </summary>
        private static JsonObject BuildGenerated(AppSettings settings)
        {
            var config = new JsonObject
            {
                ["log"] = BuildLog(settings),
                ["dns"] = BuildDns(settings),
                ["inbounds"] = BuildInbounds(settings),
                ["outbounds"] = new JsonArray(),
                ["routing"] = BuildRouting(settings)
            };

            if (IsFakeDnsActive(settings))
            {
                var pools = new JsonArray();
                AddNode(pools, new JsonObject
                {
                    ["ipPool"] = XrayConfigConstants.FakeDnsPoolV4,
                    ["poolSize"] = 65535,
                });
                AddNode(pools, new JsonObject
                {
                    ["ipPool"] = XrayConfigConstants.FakeDnsPoolV6,
                    ["poolSize"] = 65535,
                });
                config["fakedns"] = pools;
            }

            return config;
        }

        /// <summary>
        /// The starting point the profile editor offers for a new profile: the generated config
        /// for the given mode, minus outbounds. Takes no ServerEntry, so a profile can be
        /// written before any node is selected.
        /// </summary>
        public static string BuildProfileTemplate(AppSettings settings, bool tunMode)
        {
            // The slot, not the live toggle, decides which shape the template has. Clone rather
            // than flip the flag in place: settings is SettingsService's cached instance.
            var scoped = settings.Clone();
            scoped.IsTunMode = tunMode;

            var template = BuildGenerated(scoped);
            template.Remove("outbounds");

            return template.ToJsonString(JsonOpts);
        }

        /// <summary>
        /// Re-validates a profile on the way into a start. The editor already checked it, but
        /// nothing stops a user from hand-editing the file afterwards, and xray's own parse
        /// error would point at the generated config with no hint that a profile caused it.
        /// </summary>
        private static JsonObject ParseProfileOrThrow(string profileJson, bool tunMode)
        {
            var result = ConfigProfileJson.Validate(profileJson, tunMode);
            if (result.Config is not null) return result.Config;

            var detail = result.Detail ?? result.Error.ToString();
            throw new InvalidOperationException(
                Loc.Format("Error_ConfigProfileFailedMsg", ConfigProfileStore.PathFor(tunMode), detail));
        }

        /// <summary>
        /// The port SystemProxyService should write into the WinInet registry, read back out of
        /// the config that was actually built. Reading it here rather than from
        /// <see cref="AppSettings.LocalMixedPort"/> is what keeps a profile that moves the port
        /// from pointing the system proxy at a port nothing listens on.
        /// </summary>
        private static int? ResolveSystemProxyPort(JsonObject config) =>
            config["inbounds"] is JsonArray inbounds
                ? ConfigProfileJson.FindSystemProxyPort(inbounds)
                : null;

        /// <summary>True when xray will be built with a fakedns pool wired to the TUN inbound.</summary>
        private static bool IsFakeDnsActive(AppSettings settings) =>
            settings.IsTunMode && settings.FakeDnsEnabled;

        private static JsonObject BuildLog(AppSettings settings)
        {
            var log = new JsonObject
            {
                // loglevel governs the error log only. The access log ("access" unset = stdout)
                // keeps printing one [inbound -> outbound] verdict line per connection either way,
                // so proxy/direct visibility survives the quiet default.
                ["loglevel"] = XrayLogLevel.Normalize(settings.XrayLogLevel)
            };

            if (LogMaskAddress.IsEnabled(settings.LogMaskAddress))
            {
                log["maskAddress"] = settings.LogMaskAddress;
            }

            if (settings.DnsLog)
            {
                log["dnsLog"] = true;
            }

            return log;
        }

        private static JsonArray BuildInbounds(AppSettings settings)
        {
            var list = new JsonArray();

            if (settings.IsTunMode)
            {
                AddNode(list, BuildTunInbound(settings));
            }

            AddNode(list, BuildMixedInbound(settings));

            return list;
        }

        private static JsonObject BuildMixedInbound(AppSettings settings) => new()
        {
            ["tag"] = XrayConfigConstants.MixedInboundTag,
            ["protocol"] = "socks",
            ["listen"] = settings.AllowLanConnections ? "0.0.0.0" : "127.0.0.1",
            ["port"] = settings.LocalMixedPort,
            ["settings"] = new JsonObject
            {
                ["auth"] = "noauth",
                ["udp"] = true
            },
            ["sniffing"] = XrayInboundSniffing.Create()
        };

        private static JsonObject BuildTunInbound(AppSettings settings)
        {
            // IPv6 is opt-in: only when enabled do we hand the TUN a v6 gateway and hijack ::/0,
            // so IPv4-only networks keep the leak-free v4-only behaviour.
            var gateway = settings.TunIpv6Enabled
                ? CreateStringArray(XrayConfigConstants.TunGatewayV4, XrayConfigConstants.TunGatewayV6)
                : CreateStringArray(XrayConfigConstants.TunGatewayV4);
            var autoRoutes = settings.TunIpv6Enabled
                ? CreateStringArray(XrayConfigConstants.TunAutoRouteV4, XrayConfigConstants.TunAutoRouteV6)
                : CreateStringArray(XrayConfigConstants.TunAutoRouteV4);

            var tunSettings = new JsonObject
            {
                ["name"] = XrayConfigConstants.TunInterfaceName,
                ["mtu"] = XrayConfigConstants.NormalizeTunMtu(settings.TunMtu),
                ["gateway"] = gateway,
                ["autoSystemRoutingTable"] = autoRoutes,
                ["autoOutboundsInterface"] = XrayConfigConstants.TunOutboundInterfaceAuto
            };

            if (IsLeakGuardActive(settings))
            {
                // Windows queries the DNS servers of every interface, each through its own
                // interface, so routes alone do not keep lookups inside the TUN. The "dns"
                // filter blocks those; the core then requires a resolver on the TUN itself,
                // inside the gateway or routed range. The address is nominal: the port-53 lead
                // rule hands every query from tun-in to dns-out, so nothing is ever sent to it.
                tunSettings["dns"] = CreateStringArray(XrayConfigConstants.TunDnsV4);
                // "misconfigtun" blocks the IP version that has no route into the TUN, which
                // is IPv6 unless TunIpv6Enabled is on.
                tunSettings["autoSystemWfpBlockLeak"] = CreateStringArray("dns", "misconfigtun");
            }

            return new JsonObject
            {
                ["tag"] = XrayConfigConstants.TunInboundTag,
                ["protocol"] = "tun",
                ["settings"] = tunSettings,
                ["sniffing"] = XrayInboundSniffing.Create(
                    fakeDnsEnabled: settings.FakeDnsEnabled,
                    leakGuardActive: IsLeakGuardActive(settings)),
            };
        }

        /// <summary>
        /// True when DNS queries entering through the TUN are answered by xray's own DNS module
        /// (a port-53 rule to dns-out) rather than forwarded. FakeDNS needs that to hand out
        /// fake IPs; the leak guard needs it because the resolver it puts on the TUN is nominal.
        /// </summary>
        private static bool IsTunDnsHijacked(AppSettings settings) =>
            IsFakeDnsActive(settings) || IsLeakGuardActive(settings);

        /// <summary>True when the generated TUN inbound carries the WFP leak filters.</summary>
        private static bool IsLeakGuardActive(AppSettings settings) =>
            settings.IsTunMode && settings.TunLeakGuardEnabled;

        /// <summary>
        /// True when the config's TUN inbound blocks other programs' DNS outside the TUN
        /// (<c>autoSystemWfpBlockLeak</c> lists "dns"), which is what obliges dns-out to answer
        /// every query type — see <see cref="ApplyDnsForwarding"/>. Read back from the built
        /// config rather than from settings, like the system proxy port: a profile started
        /// from the template while the guard was on carries these filters in its own inbound,
        /// whatever the setting says by the time it runs.
        /// </summary>
        private static bool TunBlocksOutsideDns(JsonObject config) =>
            config["inbounds"] is JsonArray inbounds
            && inbounds.OfType<JsonObject>().Any(inbound =>
                IsString(inbound["protocol"], "tun")
                && inbound["settings"] is JsonObject tunSettings
                && tunSettings["autoSystemWfpBlockLeak"] is JsonArray leaks
                && leaks.Any(leak => IsString(leak, "dns")));

        private static bool IsString(JsonNode? node, string expected) =>
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

        /// <param name="profileMode">Set when the config came from a hand-written profile, which
        /// makes block and dns-out unconditional. Deriving the set from the tags the routing
        /// rules name would look tighter and fail badly: tags are also referenced from
        /// routing.balancers[].selector and an outbound's own sockopt.dialerProxy, and a set that
        /// misses one leaves xray refusing to start on an unknown tag. The superset fails safe
        /// instead — both outbounds are inert when nothing routes to them. Kept in agreement with
        /// <see cref="ConfigProfileJson.InjectedOutboundTags"/>, which declares the same tags as
        /// legal to reference.</param>
        /// <param name="forwardNonAddressDns">Give dns-out a real upstream for non-A/AAAA
        /// queries; see <see cref="TunBlocksOutsideDns"/>.</param>
        private static JsonArray BuildOutbounds(
            ServerEntry server,
            AppSettings settings,
            IEnumerable<ServerEntry>? availableServers,
            bool profileMode,
            bool forwardNonAddressDns)
        {
            var list = new JsonArray();

            if (server.IsChain)
            {
                var (entryServer, exitServer) = ResolveChainServers(server, availableServers);
                var proxy = BuildProxyOutbound(exitServer, ProxyOutboundTag);
                var chainEntry = BuildProxyOutbound(entryServer, ChainEntryOutboundTag);
                ApplyDialerProxy(proxy, ChainEntryOutboundTag);
                AddNode(list, proxy);
                AddNode(list, chainEntry);
            }
            else
            {
                AddNode(list, BuildProxyOutbound(server, ProxyOutboundTag));
            }

            var direct = new JsonObject
            {
                ["tag"] = DirectOutboundTag,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject()
            };

            AddNode(list, direct);

            // block outbound is needed by:
            //   1. TUN mode's UDP:443 quench rule
            //   2. Any enabled custom rule targeting "block" (smart mode only)
            bool customRulesUseBlock =
                settings.RoutingMode == "smart"
                && settings.CustomRules is { } rules
                && rules.Any(r => r.IsEnabled
                                  && r.MatchValues.Count > 0
                                  && r.OutboundTag == BlockOutboundTag);

            if (profileMode || settings.IsTunMode || customRulesUseBlock)
            {
                AddNode(list, new JsonObject
                {
                    ["tag"] = BlockOutboundTag,
                    ["protocol"] = "blackhole",
                    ["settings"] = new JsonObject()
                });
            }

            if (profileMode || IsTunDnsHijacked(settings))
            {
                var dnsOut = new JsonObject
                {
                    ["tag"] = XrayConfigConstants.DnsOutboundTag,
                    ["protocol"] = "dns",
                };

                if (forwardNonAddressDns)
                {
                    ApplyDnsForwarding(dnsOut, settings);
                }

                AddNode(list, dnsOut);
            }

            var outboundInterface = NormalizeTunOutboundInterface(settings.TunOutboundInterface);
            if (settings.IsTunMode && outboundInterface is not null)
            {
                foreach (var outbound in list.OfType<JsonObject>())
                {
                    var tag = outbound["tag"]?.GetValue<string>();
                    if (tag is ProxyOutboundTag or DirectOutboundTag or ChainEntryOutboundTag)
                    {
                        ApplyOutboundInterface(outbound, outboundInterface);
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Gives dns-out a real upstream for everything that is not an A/AAAA query. With no
        /// rules the core answers those types (TXT, SRV, MX, HTTPS...) with an empty success,
        /// which is survivable only while Windows can still ask the physical adapters' DNS
        /// servers. The leak guard blocks exactly that, leaving dns-out the one resolver other
        /// programs can reach — so it has to answer every type. The query's own destination
        /// cannot be the upstream: it is the nominal <see cref="XrayConfigConstants.TunDnsV4"/>.
        ///
        /// Forwarded queries are dialed by this outbound directly rather than routed, so they
        /// are sent through the proxy outbound explicitly; going out the physical adapter would
        /// be the very leak the guard is there to close. TCP, because not every proxy protocol
        /// carries UDP (http does not) and every public resolver answers on TCP 53.
        /// </summary>
        private static void ApplyDnsForwarding(JsonObject dnsOut, AppSettings settings)
        {
            var rules = new JsonArray();
            // Explicit, though it restates the core's default for these two types: once any
            // rule exists, an unlisted A/AAAA query would fall through to the forward below
            // and skip xray's DNS module (split-horizon servers, FakeDNS pool).
            AddNode(rules, new JsonObject
            {
                ["action"] = "hijack",
                ["qType"] = "1,28"
            });
            AddNode(rules, new JsonObject
            {
                ["action"] = "direct"
            });

            dnsOut["settings"] = new JsonObject
            {
                ["rewriteNetwork"] = "tcp",
                ["rewriteAddress"] = ForwardDnsAddress(settings.ProxyDnsServer),
                ["rewritePort"] = 53,
                ["rules"] = rules
            };

            ApplyDialerProxy(dnsOut, ProxyOutboundTag);
        }

        /// <summary>
        /// The plain-DNS address forwarded queries are rewritten to. The user's proxy resolver
        /// when it is a bare IP; a DoH/DoT URL or a host name cannot be used here (the dns
        /// outbound speaks only classic DNS, and resolving a name would need the very resolver
        /// being configured), so those fall back to the default.
        /// </summary>
        private static string ForwardDnsAddress(string? proxyDnsServer) =>
            IPAddress.TryParse(proxyDnsServer?.Trim(), out var ip) ? ip.ToString() : DefaultProxyDns;

        private const string DefaultProxyDns = "8.8.8.8";

        internal static string? NormalizeTunOutboundInterface(string? interfaceName)
        {
            if (string.IsNullOrWhiteSpace(interfaceName))
                return null;

            var value = interfaceName.Trim();
            return string.Equals(value, XrayConfigConstants.TunOutboundInterfaceAuto, StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }

        private static (ServerEntry entryServer, ServerEntry exitServer) ResolveChainServers(
            ServerEntry chain,
            IEnumerable<ServerEntry>? availableServers)
        {
            if (availableServers is null)
            {
                throw new InvalidOperationException(L.Chain_NeedServerList);
            }

            ServerEntry? entryServer = null;
            ServerEntry? exitServer = null;
            foreach (var s in availableServers)
            {
                if (entryServer is null && s.Id == chain.ChainEntryServerId) entryServer = s;
                if (exitServer is null && s.Id == chain.ChainExitServerId) exitServer = s;
                if (entryServer is not null && exitServer is not null) break;
            }

            if (entryServer is null || exitServer is null)
            {
                throw new InvalidOperationException(L.Chain_EndpointMissing);
            }

            if (entryServer.IsChain || exitServer.IsChain)
            {
                throw new InvalidOperationException(L.Chain_NoNesting);
            }

            return (entryServer, exitServer);
        }

        /// <summary>
        /// Makes <paramref name="outbound"/> dial through the outbound tagged
        /// <paramref name="tag"/>. This is the only spelling the core still accepts: outbound
        /// "proxySettings" was removed in Xray 26.9.9 and now fails config parsing outright.
        /// Unlike the interface pin below, this applies to wireguard too — the core routes that
        /// outbound's UDP through the same dialer.
        /// </summary>
        private static void ApplyDialerProxy(JsonObject outbound, string tag)
        {
            GetOrAddSockopt(outbound)["dialerProxy"] = tag;
        }

        private static void ApplyOutboundInterface(JsonObject outbound, string interfaceName)
        {
            // Wireguard outbounds carry no streamSettings, so a sockopt pin cannot apply there —
            // the process-routing rule stays their only cover. Centralized here so every caller
            // gets the exemption without repeating it.
            var protocol = outbound["protocol"]?.GetValue<string>();
            if (string.Equals(protocol, "wireguard", StringComparison.OrdinalIgnoreCase))
                return;

            GetOrAddSockopt(outbound)["interface"] = interfaceName;
        }

        // Both callers above write into the same sockopt object (a chain's exit outbound gets a
        // dialerProxy and, in TUN mode, an interface pin), so this must merge, never replace.
        private static JsonObject GetOrAddSockopt(JsonObject outbound)
        {
            var streamSettings = outbound["streamSettings"] as JsonObject;
            if (streamSettings is null)
            {
                streamSettings = new JsonObject();
                outbound["streamSettings"] = streamSettings;
            }

            var sockopt = streamSettings["sockopt"] as JsonObject;
            if (sockopt is null)
            {
                sockopt = new JsonObject();
                streamSettings["sockopt"] = sockopt;
            }

            return sockopt;
        }

        private static JsonObject BuildProxyOutbound(ServerEntry server, string tag)
        {
            return server.Protocol.ToLowerInvariant() switch
            {
                "vmess" => BuildVmessOutbound(server, tag),
                "vless" => BuildVlessOutbound(server, tag),
                "hysteria2" => BuildHysteria2Outbound(server, tag),
                "trojan" => BuildTrojanOutbound(server, tag),
                "socks" => BuildSocksOutbound(server, tag),
                "http" => BuildHttpOutbound(server, tag),
                "wireguard" => BuildWireguardOutbound(server, tag),
                _ => BuildSsOutbound(server, tag)
            };
        }

        private static JsonObject BuildSsOutbound(ServerEntry server, string tag)
        {
            var servers = new JsonArray();
            AddNode(servers, new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
                ["method"] = server.Encryption,
                ["password"] = server.Password
            });

            var outbound = new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "shadowsocks",
                ["settings"] = new JsonObject
                {
                    ["servers"] = servers
                },
                ["streamSettings"] = new JsonObject
                {
                    ["network"] = "tcp"
                }
            };

            ApplyFinalmask((JsonObject)outbound["streamSettings"]!, server);
            return outbound;
        }

        private static JsonObject BuildVmessOutbound(ServerEntry server, string tag)
        {
            var users = new JsonArray();
            AddNode(users, new JsonObject
            {
                ["id"] = server.Uuid,
                ["alterId"] = server.AlterId,
                ["security"] = "auto"
            });

            var vnext = new JsonArray();
            AddNode(vnext, new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
                ["users"] = users
            });

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "vmess",
                ["settings"] = new JsonObject
                {
                    ["vnext"] = vnext
                },
                ["streamSettings"] = BuildStreamSettings(server)
            };
        }

        private static JsonObject BuildVlessOutbound(ServerEntry server, string tag)
        {
            var user = new JsonObject
            {
                ["id"] = server.Uuid,
                ["encryption"] = string.IsNullOrEmpty(server.VlessEncryption) ? "none" : server.VlessEncryption
            };

            if (!string.IsNullOrWhiteSpace(server.Flow))
            {
                user["flow"] = server.Flow;
            }

            var users = new JsonArray();
            AddNode(users, user);

            var vnext = new JsonArray();
            AddNode(vnext, new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
                ["users"] = users
            });

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "vless",
                ["settings"] = new JsonObject
                {
                    ["vnext"] = vnext
                },
                ["streamSettings"] = BuildStreamSettings(server)
            };
        }

        private static JsonObject BuildHysteria2Outbound(ServerEntry server, string tag)
        {
            var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni;

            // hysteria2 forks the *stream* shape, not the TLS block underneath — no uTLS
            // fingerprint because it is QUIC, everything else identical to the shared path.
            var tlsSettings = BuildTlsSettings(server, sni);

            var streamSettings = new JsonObject
            {
                ["network"] = "hysteria",
                ["security"] = "tls",
                ["tlsSettings"] = tlsSettings,
                ["hysteriaSettings"] = new JsonObject
                {
                    ["version"] = 2,
                    ["auth"] = server.Password
                }
            };
            ApplyFinalmask(streamSettings, server);

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "hysteria",
                ["settings"] = new JsonObject
                {
                    ["version"] = 2,
                    ["address"] = server.Host,
                    ["port"] = server.Port
                },
                ["streamSettings"] = streamSettings
            };
        }

        private static JsonObject BuildTrojanOutbound(ServerEntry server, string tag)
        {
            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "trojan",
                ["settings"] = new JsonObject
                {
                    ["address"] = server.Host,
                    ["port"] = server.Port,
                    ["password"] = server.Password
                },
                ["streamSettings"] = BuildStreamSettings(server)
            };
        }

        private static JsonObject BuildSocksOutbound(ServerEntry server, string tag)
        {
            var serverObject = new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
            };

            if (!string.IsNullOrWhiteSpace(server.Username)
                || !string.IsNullOrWhiteSpace(server.Password))
            {
                var users = new JsonArray();
                AddNode(users, new JsonObject
                {
                    ["user"] = server.Username,
                    ["pass"] = server.Password,
                });
                serverObject["users"] = users;
            }

            var servers = new JsonArray();
            AddNode(servers, serverObject);

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["servers"] = servers
                }
            };
        }

        private static JsonObject BuildHttpOutbound(ServerEntry server, string tag)
        {
            var serverObject = new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
            };

            if (!string.IsNullOrWhiteSpace(server.Username)
                || !string.IsNullOrWhiteSpace(server.Password))
            {
                var users = new JsonArray();
                AddNode(users, new JsonObject
                {
                    ["user"] = server.Username,
                    ["pass"] = server.Password,
                });
                serverObject["users"] = users;
            }

            var servers = new JsonArray();
            AddNode(servers, serverObject);

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "http",
                ["settings"] = new JsonObject
                {
                    ["servers"] = servers
                }
            };
        }

        private static JsonObject BuildWireguardOutbound(ServerEntry server, string tag)
        {
            var address = CreateStringArray(
                server.WgLocalAddress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var peer = new JsonObject
            {
                ["publicKey"] = server.WgPublicKey,
                ["endpoint"] = FormatEndpoint(server.Host, server.Port),
                ["allowedIPs"] = CreateStringArray("0.0.0.0/0", "::/0"),
                ["keepAlive"] = 0
            };
            if (!string.IsNullOrWhiteSpace(server.WgPreSharedKey))
            {
                peer["preSharedKey"] = server.WgPreSharedKey;
            }

            var peers = new JsonArray();
            AddNode(peers, peer);

            // No domainStrategy: the core removed it from the wireguard outbound. Domain targets
            // are resolved through the tunnel by its remoteDNS, whose default list is kept.
            var settings = new JsonObject
            {
                ["secretKey"] = server.WgPrivateKey,
                ["peers"] = peers
            };

            // Omit address entirely when no CIDR was configured: an explicit "address": [] reads as
            // "the user set zero local addresses", whereas omitting it lets xray fall back to its
            // built-in default. Mirrors the omit-when-empty handling of mtu/reserved/preSharedKey.
            if (address.Count > 0)
            {
                settings["address"] = address;
            }

            if (server.WgMtu > 0)
            {
                settings["mtu"] = server.WgMtu;
            }

            var reserved = ParseWireguardReserved(server.WgReserved);
            if (reserved is not null)
            {
                settings["reserved"] = reserved;
            }

            return new JsonObject
            {
                ["tag"] = tag,
                ["protocol"] = "wireguard",
                ["settings"] = settings
            };
        }

        /// <summary>Wraps a bare IPv6 literal in brackets so "host:port" stays unambiguous.</summary>
        private static string FormatEndpoint(string host, int port)
        {
            var formatted = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
            return $"{formatted}:{port}";
        }

        /// <summary>
        /// Parses xray's WireGuard reserved bytes into a 3-int JsonArray. Accepts both the integer
        /// form ("209,98,59") and the base64 scalar form ("U4An", common for Cloudflare WARP nodes);
        /// returns null when it resolves to neither exactly three integers nor exactly three bytes.
        /// </summary>
        private static JsonArray? ParseWireguardReserved(string reserved)
        {
            if (string.IsNullOrWhiteSpace(reserved))
                return null;

            var trimmed = reserved.Trim();

            // Integer form: exactly three comma-separated ints.
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts.All(p => int.TryParse(p, out _)))
            {
                return ToReservedArray(parts.Select(int.Parse));
            }

            // Base64 form: WARP-style "U4An" decodes to the three reserved bytes. Dropping it would
            // silently omit settings.reserved and break the handshake for those nodes.
            try
            {
                var bytes = Convert.FromBase64String(trimmed);
                if (bytes.Length == 3)
                {
                    return ToReservedArray(bytes.Select(b => (int)b));
                }
            }
            catch (FormatException)
            {
            }

            return null;
        }

        private static JsonArray ToReservedArray(IEnumerable<int> values)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add((JsonNode?)JsonValue.Create(value));
            }
            return array;
        }

        /// <summary>
        /// The tlsSettings keys that are the same for every TLS outbound. hysteria2 builds its own
        /// streamSettings because its stream shape differs, but the TLS block underneath is the
        /// same contract — routing both through here is what stops each new TLS field from having
        /// to be written twice and drifting. Transport-specific keys (uTLS fingerprint, ECH) are
        /// added by the caller.
        /// </summary>
        private static JsonObject BuildTlsSettings(ServerEntry server, string sni)
        {
            var tlsSettings = new JsonObject
            {
                ["serverName"] = sni,
                ["allowInsecure"] = server.AllowInsecure
            };

            // A node masquerading behind someone else's serverName authenticates by certificate
            // fingerprint instead of by chain. Protocol-agnostic; REALITY is unaffected because it
            // authenticates with the x25519 public key and has no tlsSettings at all.
            if (!string.IsNullOrWhiteSpace(server.PinnedPeerCertSha256))
            {
                tlsSettings["pinnedPeerCertSha256"] = server.PinnedPeerCertSha256;
            }

            // The core verifies against its own bundled CA list by default and no longer
            // consults the Windows certificate store, so a node signed by a private CA the user
            // installed there needs this to keep verifying.
            if (server.UseSystemCa)
            {
                tlsSettings["useSystemCA"] = true;
            }

            return tlsSettings;
        }

        private static JsonObject BuildStreamSettings(ServerEntry server)
        {
            var network = string.IsNullOrWhiteSpace(server.Network)
                ? "tcp"
                : server.Network.ToLowerInvariant();
            var security = string.IsNullOrWhiteSpace(server.Security)
                ? "none"
                : server.Security.ToLowerInvariant();

            var stream = new JsonObject
            {
                ["network"] = network,
                ["security"] = security
            };

            if (security == "tls")
            {
                // CDN/tunnel nodes (Host header != connect address) need the SNI to match the
                // Host header when no explicit SNI is given, or the edge can't route the TLS
                // handshake. Same fallback rule as v2rayN; only ws/xhttp carry a Host header.
                var hostHeader = (network == "ws" || network == "xhttp") ? server.WsHost : null;
                var sni = !string.IsNullOrWhiteSpace(server.Sni) ? server.Sni
                    : !string.IsNullOrWhiteSpace(hostHeader) ? hostHeader
                    : server.Host;
                var tlsSettings = BuildTlsSettings(server, sni);
                // uTLS fingerprint applies to the TCP-based transports only, so it stays here
                // rather than in the shared builder that hysteria2 also goes through.
                tlsSettings["fingerprint"] =
                    string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint;

                if (string.Equals(server.Protocol, "vless", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(server.EchConfigList))
                {
                    // No echForceQuery beside it: the core dropped that field (ECH is forced
                    // whenever a config list is set) and silently ignores it now.
                    tlsSettings["echConfigList"] = server.EchConfigList;
                }

                stream["tlsSettings"] = tlsSettings;
            }
            else if (security == "reality")
            {
                var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni;
                var fingerprint = string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint;
                var spiderX = string.IsNullOrWhiteSpace(server.SpiderX) ? "/" : server.SpiderX;

                stream["realitySettings"] = new JsonObject
                {
                    ["serverName"] = sni,
                    ["fingerprint"] = fingerprint,
                    ["publicKey"] = server.PublicKey,
                    ["shortId"] = server.ShortId,
                    ["spiderX"] = spiderX
                };
            }

            if (network == "ws")
            {
                var wsSettings = new JsonObject
                {
                    ["path"] = server.Path
                };

                // The standalone "host" key, same as xhttp below. The older headers.Host
                // spelling still loads but logs a "will be removed soon" deprecation warning.
                if (!string.IsNullOrWhiteSpace(server.WsHost))
                {
                    wsSettings["host"] = server.WsHost;
                }

                stream["wsSettings"] = wsSettings;
            }
            else if (network == "grpc")
            {
                stream["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = server.Path
                };
            }
            else if (network == "xhttp")
            {
                var settings = new JsonObject
                {
                    ["path"] = server.Path
                };

                if (!string.IsNullOrWhiteSpace(server.WsHost))
                {
                    settings["host"] = server.WsHost;
                }

                var mode = XhttpSettings.NormalizeMode(server.XhttpMode);

                if (FinalmaskJson.Parse(server.XhttpExtra) is JsonObject extra)
                {
                    NormalizeXhttpDownloadSettings(extra);
                    settings["extra"] = extra;

                    // xray refuses to load stream-one combined with a download split ("Can not
                    // use "downloadSettings" in "stream-one" mode"). The split server is the
                    // intent-bearing half of that contradiction, so keep it and omit the mode —
                    // auto picks a split-compatible one.
                    if (mode == XhttpSettings.StreamOne && extra["downloadSettings"] is JsonObject)
                    {
                        mode = string.Empty;
                    }
                }

                if (!string.IsNullOrEmpty(mode))
                {
                    settings["mode"] = mode;
                }

                stream["xhttpSettings"] = settings;
            }

            ApplyFinalmask(stream, server);
            return stream;
        }

        private static void ApplyFinalmask(JsonObject streamSettings, ServerEntry server)
        {
            var finalmask = FinalmaskJson.Parse(server.Finalmask);
            if (finalmask is JsonObject root)
            {
                FinalmaskJson.TranslateLegacyUdpHop(root);
                streamSettings["finalmask"] = root;
            }
        }

        // v2board panels emit v2rayN-compact downloadSettings inside extra ({"server","servername","path","port"}),
        // but xray-core wants a StreamConfig-shaped object (address + network/security/tlsSettings/xhttpSettings).
        // xray silently ignores the unknown compact keys, leaving address empty — the download leg never comes up
        // even though `-test` reports Configuration OK. Translated at emit time only, so the stored/shared extra
        // stays byte-faithful to what the subscription sent.
        private static void NormalizeXhttpDownloadSettings(JsonObject extra)
        {
            if (extra["downloadSettings"] is not JsonObject download)
                return;

            var isCompact = download["server"] is not null
                || download["servername"] is not null
                || download["path"] is not null;
            if (!isCompact)
                return;

            if (download["address"] is null && download["server"] is JsonNode address)
            {
                // Detach before re-parenting — JsonNode enforces a single parent.
                download.Remove("server");
                download["address"] = address;
            }

            if (download["network"] is null)
            {
                download["network"] = "xhttp";
            }

            if (download["xhttpSettings"] is null && download["path"] is JsonNode path)
            {
                download.Remove("path");
                // The download leg carries no mode — only the upload leg negotiates it.
                download["xhttpSettings"] = new JsonObject { ["path"] = path };
            }

            if (download["tlsSettings"] is null && download["servername"] is JsonNode serverName)
            {
                download.Remove("servername");
                if (download["security"] is null)
                {
                    download["security"] = "tls";
                }
                download["tlsSettings"] = new JsonObject { ["serverName"] = serverName };
            }
        }

        private static JsonObject BuildRouting(AppSettings settings)
        {
            // Global mode bypasses both AdvancedRouting and the smart-mode default template;
            // it always force-routes everything to the proxy outbound after the TUN prefix.
            if (settings.RoutingMode == "global")
            {
                return BuildGlobalRouting(settings);
            }

            // Smart mode: AdvancedRouting (if set) replaces the default routing template.
            // TUN lead rules and CustomRules are merged on top, so the user cannot lock
            // themselves out of TUN-required system traffic by writing a bad advanced JSON.
            var hasAdvancedRouting = settings.AdvancedRouting is not null;
            var baseRouting = hasAdvancedRouting
                ? (JsonObject)settings.AdvancedRouting!.DeepClone()
                : BuildDefaultRoutingTemplate(settings, includeFallback: false);

            // baseRouting is exclusively owned (fresh clone or fresh build). Build a fresh rules
            // array so the TUN lead rules can be put in front of the user's own.
            var baseRules = baseRouting["rules"] as JsonArray ?? new JsonArray();
            var rules = BuildSmartRules(settings, baseRules);
            baseRouting["rules"] = rules;

            if (!hasAdvancedRouting)
            {
                AddDefaultProxyFallbackRule(rules);
            }

            if (baseRouting["domainStrategy"] is null)
            {
                baseRouting["domainStrategy"] = "AsIs";
            }

            return baseRouting;
        }

        private static JsonObject BuildGlobalRouting(AppSettings settings)
        {
            var rules = new JsonArray();
            AppendTunLeadRules(rules, settings);

            AppendTunUdp443BlockRule(rules, settings);

            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = ProxyOutboundTag,
                ["network"] = "tcp,udp"
            });

            return new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = rules
            };
        }

        /// <summary>
        /// Builds smart-mode rules. In TUN mode, process rules from both the UI and
        /// AdvancedRouting are promoted before the UDP/443 quench rule so explicit
        /// per-process bypasses for QUIC-based clients are not shadowed.
        /// </summary>
        private static JsonArray BuildSmartRules(AppSettings settings, JsonArray baseRules)
        {
            var rules = new JsonArray();

            if (settings.IsTunMode)
            {
                AppendTunLeadRules(rules, settings);
                AddCustomRules(rules, settings.CustomRules, IsProcessCustomRule);
                AddClonedRules(rules, baseRules, IsProcessRoutingRule);
                AppendTunUdp443BlockRule(rules, settings);
                AddCustomRules(rules, settings.CustomRules, rule => !IsProcessCustomRule(rule));
                AddClonedRules(rules, baseRules, rule => !IsProcessRoutingRule(rule));
            }
            else
            {
                AddCustomRules(rules, settings.CustomRules, _ => true);
                AddClonedRules(rules, baseRules, _ => true);
            }

            return rules;
        }

        /// <summary>
        /// Adds the fixed TUN lead rules that must stay before user/advanced rules:
        /// FakeDNS DNS capture first, then xray/self direct.
        /// </summary>
        private static void AppendTunLeadRules(JsonArray rules, AppSettings settings)
        {
            if (!settings.IsTunMode) return;

            if (IsTunDnsHijacked(settings))
            {
                // Must precede the self/xray direct rule so DNS queries from tun-in get
                // intercepted by xray's internal DNS handler (and the fakedns pool) rather
                // than being forwarded upstream.
                AddNode(rules, new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = CreateStringArray(XrayConfigConstants.TunInboundTag),
                    ["port"] = "53",
                    ["outboundTag"] = XrayConfigConstants.DnsOutboundTag,
                });
            }

            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = DirectOutboundTag,
                // Keep the plain process-name fallback as well as Xray's path sugars. Process
                // attribution for a second helper core can occasionally lack the full path.
                ["process"] = CreateStringArray("self/", "xray/", "xray")
            });
        }

        private static void AppendTunUdp443BlockRule(JsonArray rules, AppSettings settings)
        {
            if (!settings.IsTunMode) return;

            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = BlockOutboundTag,
                ["network"] = "udp",
                ["port"] = "443"
            });
        }

        private static void AddCustomRules(
            JsonArray rules,
            IEnumerable<CustomRoutingRule>? customRules,
            Func<CustomRoutingRule, bool> predicate)
        {
            if (customRules is null) return;

            foreach (var rule in customRules)
            {
                if (!rule.IsEnabled || rule.MatchValues.Count == 0 || !predicate(rule))
                    continue;

                AddNode(rules, CustomRuleToJsonObject(rule));
            }
        }

        private static void AddClonedRules(
            JsonArray rules,
            JsonArray sourceRules,
            Func<JsonNode?, bool> predicate)
        {
            foreach (var rule in sourceRules)
            {
                if (rule is null || !predicate(rule))
                    continue;

                AddNode(rules, rule.DeepClone());
            }
        }

        private static bool IsProcessCustomRule(CustomRoutingRule rule) => rule.Type == "process";

        private static bool IsProcessRoutingRule(JsonNode? rule) =>
            rule is JsonObject ruleObject && ruleObject["process"] is not null;

        /// <summary>
        /// The default smart-mode routing object — proxy Google, direct domestic geosite/geoip
        /// (per settings.RoutingRegion), fallback everything else to proxy. Returned as a fresh JsonObject so callers can
        /// either inject it into the live xray config or persist it as the seed of
        /// settings.AdvancedRouting (the "advanced editor" template).
        /// </summary>
        public static JsonObject BuildDefaultRoutingTemplate(AppSettings settings, bool includeFallback = true)
        {
            var rules = new JsonArray();

            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = ProxyOutboundTag,
                ["domain"] = CreateStringArray("geosite:google")
            });
            var (geositeDomestic, geoipDomestic) = RegionGeoTokens(settings.RoutingRegion);
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = DirectOutboundTag,
                ["domain"] = CreateStringArray(geositeDomestic, "geosite:private")
            });
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = DirectOutboundTag,
                ["ip"] = CreateStringArray(geoipDomestic, "geoip:private")
            });
            if (includeFallback)
            {
                AddDefaultProxyFallbackRule(rules);
            }

            return new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = rules
            };
        }

        private static void AddDefaultProxyFallbackRule(JsonArray rules)
        {
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = ProxyOutboundTag,
                ["network"] = "tcp,udp"
            });
        }

        /// <summary>
        /// Maps a routing region code to its domestic geosite/geoip tokens. "cn" (default) uses
        /// geosite:cn / geoip:cn; "ru"/"ir" use geosite:category-ru|ir + geoip:ru|ir — the shipped
        /// geosite.dat (Loyalsoldier) has no bare geosite:ru / geosite:ir, only the category-* lists.
        /// </summary>
        private static (string geosite, string geoip) RegionGeoTokens(string? region) => region switch
        {
            "ru" => ("geosite:category-ru", "geoip:ru"),
            "ir" => ("geosite:category-ir", "geoip:ir"),
            _    => ("geosite:cn", "geoip:cn"),
        };

        /// <summary>
        /// Default "direct" DNS resolver for the selected region, used only when the user hasn't set
        /// <see cref="AppSettings.DirectDnsServer"/>. CN uses AliDNS; RU/IR use well-known
        /// in-country public resolvers (Yandex / Shecan).
        ///
        /// Deliberately not split by TUN mode. It used to be (223.5.5.5 under TUN, 114.114.114.114
        /// otherwise), which was residue from the original two hardcoded server lists rather than a
        /// decision: DNS here is split-horizon either way — domestic domains to this resolver,
        /// everything else to the proxy resolver — so leak prevention comes from that structure,
        /// not from which domestic resolver answers. The RU and IR arms never made the distinction
        /// either. AliDNS is the better single default: anycast, and it supports EDNS Client Subnet,
        /// which 114DNS does not, so CDN answers land geographically closer.
        /// </summary>
        private static string DefaultDirectDns(string? region) => region switch
        {
            "ru" => "77.88.8.8",
            "ir" => "178.22.122.100",
            _    => "223.5.5.5",
        };

        private static JsonObject CustomRuleToJsonObject(CustomRoutingRule rule)
        {
            var node = new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = rule.OutboundTag,
            };
            switch (rule.Type)
            {
                case "ip":      node["ip"]      = CreateStringArray(rule.MatchValues); break;
                case "process": node["process"] = CreateStringArray(rule.MatchValues); break;
                default:        node["domain"]  = CreateStringArray(rule.MatchValues); break;
            }
            return node;
        }

        private static JsonObject BuildDns(AppSettings settings)
        {
            var (geositeDomestic, _) = RegionGeoTokens(settings.RoutingRegion);
            var directDns = settings.DirectDnsServer ?? DefaultDirectDns(settings.RoutingRegion);
            var proxyDns = settings.ProxyDnsServer ?? DefaultProxyDns;

            var directEntry = new JsonObject
            {
                ["address"]      = directDns,
                ["domains"]      = CreateStringArray(geositeDomestic, "geosite:private"),
                ["skipFallback"] = true,
            };


            var proxyEntry = new JsonObject
            {
                ["address"] = proxyDns,
            };

            var servers = new JsonArray();
            if (IsFakeDnsActive(settings))
            {
                // FakeDNS must be first: it answers initial client lookups with fake IPs. The
                // real DNS entries below handle outbound-side resolution after sniffing recovers
                // the original domain.
                AddValue(servers, XrayConfigConstants.FakeDnsServerTag);
            }
            AddNode(servers, directEntry);
            AddNode(servers, proxyEntry);

            return new JsonObject
            {
                ["servers"]       = servers,
                ["queryStrategy"] = settings.DnsQueryStrategy,
                ["disableCache"]  = !settings.DnsCacheEnabled
            };
        }

        private static JsonArray CreateStringArray(params string[] values)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                AddValue(array, value);
            }

            return array;
        }

        private static JsonArray CreateStringArray(IEnumerable<string> values)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                AddValue(array, value);
            }

            return array;
        }

        private static void AddNode(JsonArray array, JsonNode node)
        {
            array.Add(node);
        }

        private static void AddValue(JsonArray array, string value)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        private const string SpeedtestOutboundTagPrefix = "out-";

        /// <summary>
        /// Maps an outbound tag of a <see cref="BuildSpeedtestConfig"/> config back to the index
        /// of the entry it was built from, or -1 when the tag is not one of those (null, the
        /// freedom fallback). Lets the latency probe find the node the core rejected by the tag
        /// in the core's own error, without knowing the tag scheme.
        /// </summary>
        public static int SpeedtestEntryIndex(string? outboundTag) =>
            outboundTag is not null
            && outboundTag.StartsWith(SpeedtestOutboundTagPrefix, StringComparison.Ordinal)
            && int.TryParse(
                outboundTag.AsSpan(SpeedtestOutboundTagPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var index)
                ? index
                : -1;

        /// <summary>
        /// Builds a dedicated speed-test config for the "real delay" latency test: one socks
        /// inbound + one proxy outbound per server, paired 1:1 via routing (in-{i} → out-{i}).
        /// Run in a throwaway core separate from the live connection. No TUN/DNS/fakedns — just
        /// enough to route an HTTP probe through each server. Chain servers are not supported;
        /// the caller must filter them out.
        /// </summary>
        /// <param name="entries">Each server paired with the local socks port it should listen on.</param>
        /// <param name="outboundInterface">Resolved physical interface name to pin every proxy
        /// outbound to (see <see cref="TunService.ResolveOutboundInterface"/>), or null for no
        /// pin. The pin is required when another Xray process owns a full-route TUN, otherwise
        /// this helper core's node connections can be captured and proxy-looped. Taken verbatim —
        /// callers resolve the "auto" sentinel first.</param>
        public static string BuildSpeedtestConfig(
            IReadOnlyList<(ServerEntry server, int port)> entries,
            string? outboundInterface)
        {
            var inbounds = new JsonArray();
            var outbounds = new JsonArray();
            var rules = new JsonArray();

            for (int i = 0; i < entries.Count; i++)
            {
                var (server, port) = entries[i];
                var inTag = $"in-{i}";
                var outTag = $"{SpeedtestOutboundTagPrefix}{i}";

                AddNode(inbounds, new JsonObject
                {
                    ["tag"] = inTag,
                    ["protocol"] = "socks",
                    ["listen"] = "127.0.0.1",
                    ["port"] = port,
                    ["settings"] = new JsonObject
                    {
                        ["auth"] = "noauth",
                        ["udp"] = false
                    }
                });

                var outbound = BuildProxyOutbound(server, outTag);
                if (outboundInterface is not null)
                {
                    ApplyOutboundInterface(outbound, outboundInterface);
                }
                AddNode(outbounds, outbound);

                AddNode(rules, new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = CreateStringArray(inTag),
                    ["outboundTag"] = outTag
                });
            }

            // Freedom fallback so any unmatched traffic inside the core has somewhere to go.
            AddNode(outbounds, new JsonObject
            {
                ["tag"] = DirectOutboundTag,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject()
            });

            var config = new JsonObject
            {
                // Keep ≥ warning visibility: XrayReadySignal needs the "started" line.
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["inbounds"] = inbounds,
                ["outbounds"] = outbounds,
                ["routing"] = new JsonObject
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = rules
                }
            };

            return config.ToJsonString(JsonOpts);
        }
    }
}
