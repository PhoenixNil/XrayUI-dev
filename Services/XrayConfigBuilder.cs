using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Builds an xray-core JSON configuration string for the given server and app settings.
    /// Uses JsonObject/JsonArray so Native AOT does not need reflection-based serialization.
    /// </summary>
    public static class XrayConfigBuilder
    {
        private const string ProxyOutboundTag = "proxy";
        private const string DirectOutboundTag = "direct";
        private const string BlockOutboundTag = "block";
        private const string ChainEntryOutboundTag = "chain-entry";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        public static string Build(
            ServerEntry server,
            AppSettings settings,
            IEnumerable<ServerEntry>? availableServers = null)
        {
            var config = new JsonObject
            {
                ["log"] = BuildLog(settings),
                ["dns"] = BuildDns(settings),
                ["inbounds"] = BuildInbounds(settings),
                ["outbounds"] = BuildOutbounds(server, settings, availableServers),
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

            return config.ToJsonString(JsonOpts);
        }

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

            AddNode(list, new JsonObject
            {
                ["tag"] = XrayConfigConstants.MixedInboundTag,
                ["protocol"] = "socks",
                ["listen"] = settings.AllowLanConnections ? "0.0.0.0" : "127.0.0.1",
                ["port"] = settings.LocalMixedPort,
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = true
                }
            });

            return list;
        }

        private static JsonObject BuildTunInbound(AppSettings settings)
        {
            var destOverride = settings.FakeDnsEnabled
                ? CreateStringArray(XrayConfigConstants.FakeDnsServerTag, "http", "tls", "quic")
                : CreateStringArray("http", "tls", "quic");

            var sniffing = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = destOverride,
            };
            if (settings.FakeDnsEnabled)
            {
                sniffing["metadataOnly"] = false;
            }

            // IPv6 is opt-in: only when enabled do we hand the TUN a v6 gateway and hijack ::/0,
            // so IPv4-only networks keep the leak-free v4-only behaviour.
            var gateway = settings.TunIpv6Enabled
                ? CreateStringArray(XrayConfigConstants.TunGatewayV4, XrayConfigConstants.TunGatewayV6)
                : CreateStringArray(XrayConfigConstants.TunGatewayV4);
            var autoRoutes = settings.TunIpv6Enabled
                ? CreateStringArray(XrayConfigConstants.TunAutoRouteV4, XrayConfigConstants.TunAutoRouteV6)
                : CreateStringArray(XrayConfigConstants.TunAutoRouteV4);

            return new JsonObject
            {
                ["tag"] = XrayConfigConstants.TunInboundTag,
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = XrayConfigConstants.TunInterfaceName,
                    ["mtu"] = XrayConfigConstants.NormalizeTunMtu(settings.TunMtu),
                    ["gateway"] = gateway,
                    ["autoSystemRoutingTable"] = autoRoutes,
                    ["autoOutboundsInterface"] = XrayConfigConstants.TunOutboundInterfaceAuto
                },
                ["sniffing"] = sniffing,
            };
        }

        private static JsonArray BuildOutbounds(
            ServerEntry server,
            AppSettings settings,
            IEnumerable<ServerEntry>? availableServers)
        {
            var list = new JsonArray();

            if (server.IsChain)
            {
                var (entryServer, exitServer) = ResolveChainServers(server, availableServers);
                var proxy = BuildProxyOutbound(exitServer, ProxyOutboundTag);
                var chainEntry = BuildProxyOutbound(entryServer, ChainEntryOutboundTag);
                ApplyProxySettings(proxy, ChainEntryOutboundTag);
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

            if (settings.IsTunMode || customRulesUseBlock)
            {
                AddNode(list, new JsonObject
                {
                    ["tag"] = BlockOutboundTag,
                    ["protocol"] = "blackhole",
                    ["settings"] = new JsonObject()
                });
            }

            if (IsFakeDnsActive(settings))
            {
                AddNode(list, new JsonObject
                {
                    ["tag"] = XrayConfigConstants.DnsOutboundTag,
                    ["protocol"] = "dns",
                });
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

        private static void ApplyProxySettings(JsonObject outbound, string tag)
        {
            outbound["proxySettings"] = new JsonObject
            {
                ["tag"] = tag,
                ["transportLayer"] = true
            };
        }

        private static void ApplyOutboundInterface(JsonObject outbound, string interfaceName)
        {
            // Wireguard outbounds carry no streamSettings, so a sockopt pin cannot apply there —
            // the process-routing rule stays their only cover. Centralized here so every caller
            // gets the exemption without repeating it.
            var protocol = outbound["protocol"]?.GetValue<string>();
            if (string.Equals(protocol, "wireguard", StringComparison.OrdinalIgnoreCase))
                return;

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

            sockopt["interface"] = interfaceName;
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

            var streamSettings = new JsonObject
            {
                ["network"] = "hysteria",
                ["security"] = "tls",
                ["tlsSettings"] = new JsonObject
                {
                    ["serverName"] = sni,
                    ["allowInsecure"] = server.AllowInsecure
                },
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

            var settings = new JsonObject
            {
                ["secretKey"] = server.WgPrivateKey,
                ["peers"] = peers,
                ["domainStrategy"] = "ForceIP"
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
                var fingerprint = string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint;
                var tlsSettings = new JsonObject
                {
                    ["serverName"] = sni,
                    ["fingerprint"] = fingerprint,
                    ["allowInsecure"] = server.AllowInsecure
                };

                if (string.Equals(server.Protocol, "vless", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(server.EchConfigList))
                {
                    tlsSettings["echConfigList"] = server.EchConfigList;

                    var echForceQuery = EchSettings.NormalizeForceQuery(server.EchForceQuery);
                    if (!string.IsNullOrEmpty(echForceQuery))
                    {
                        tlsSettings["echForceQuery"] = echForceQuery;
                    }
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
                JsonObject headers;
                if (string.IsNullOrWhiteSpace(server.WsHost))
                {
                    headers = [];
                }
                else
                {
                    headers = new JsonObject
                    {
                        ["Host"] = server.WsHost
                    };
                }

                stream["wsSettings"] = new JsonObject
                {
                    ["path"] = server.Path,
                    ["headers"] = headers
                };
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
            if (finalmask is JsonObject)
            {
                streamSettings["finalmask"] = finalmask;
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
            // TUN prefix rules and CustomRules are merged on top, so the user cannot lock
            // themselves out of TUN-required system traffic by writing a bad advanced JSON.
            var hasAdvancedRouting = settings.AdvancedRouting is not null;
            var baseRouting = hasAdvancedRouting
                ? (JsonObject)settings.AdvancedRouting!.DeepClone()
                : BuildDefaultRoutingTemplate(settings, includeFallback: false);

            // baseRouting is exclusively owned (fresh clone or fresh build). Build a fresh
            // rules array so TUN process bypass rules can sit before the UDP/443 quench rule,
            // while ordinary domain/IP rules still remain behind it.
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

            if (settings.FakeDnsEnabled)
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
        /// <see cref="AppSettings.DirectDnsServer"/>. CN keeps the existing fast domestic resolvers;
        /// RU/IR use well-known in-country public resolvers (Yandex / Shecan).
        /// </summary>
        private static string DefaultDirectDns(string? region, bool tunMode) => region switch
        {
            "ru" => "77.88.8.8",
            "ir" => "178.22.122.100",
            _    => tunMode ? "223.5.5.5" : "114.114.114.114",
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
            var directDns = settings.DirectDnsServer
                ?? DefaultDirectDns(settings.RoutingRegion, settings.IsTunMode);
            var proxyDns = settings.ProxyDnsServer ?? "8.8.8.8";

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
                var outTag = $"out-{i}";

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
