using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Builds an xray-core JSON configuration string for the given server and app settings.
    /// Uses JsonObject/JsonArray so Native AOT does not need reflection-based serialization.
    /// </summary>
    public static class XrayConfigBuilder
    {
        private const string DefaultLogLevel = "info";
        private const string ProxyOutboundTag = "proxy";
        private const string DirectOutboundTag = "direct";
        private const string ChainEntryOutboundTag = "chain-entry";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        public static string Build(
            ServerEntry server,
            AppSettings settings,
            string? tunOutboundInterfaceName = null,
            IEnumerable<ServerEntry>? availableServers = null)
        {
            var config = new JsonObject
            {
                ["log"] = BuildLog(settings),
                ["dns"] = BuildDns(settings),
                ["inbounds"] = BuildInbounds(settings),
                ["outbounds"] = BuildOutbounds(server, settings, tunOutboundInterfaceName, availableServers),
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
                ["loglevel"] = DefaultLogLevel
            };

            if (LogMaskAddress.IsEnabled(settings.LogMaskAddress))
            {
                log["maskAddress"] = settings.LogMaskAddress;
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
                ["listen"] = "127.0.0.1",
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

            return new JsonObject
            {
                ["tag"] = XrayConfigConstants.TunInboundTag,
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = "xray-tun",
                    ["MTU"] = 9000,
                    ["gateway"] = CreateStringArray("172.18.0.1/30"),
                    ["autoSystemRoutingTable"] = CreateStringArray("0.0.0.0/0"),
                    ["autoOutboundsInterface"] = "auto"
                },
                ["sniffing"] = sniffing,
            };
        }

        private static JsonArray BuildOutbounds(
            ServerEntry server,
            AppSettings settings,
            string? tunOutboundInterfaceName,
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
                                  && !string.IsNullOrWhiteSpace(r.Match)
                                  && r.OutboundTag == "block");

            if (settings.IsTunMode || customRulesUseBlock)
            {
                AddNode(list, new JsonObject
                {
                    ["tag"] = "block",
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

            if (settings.IsTunMode && !string.IsNullOrWhiteSpace(tunOutboundInterfaceName))
            {
                // sockopt.interface only matters for outbounds that actually open sockets
                // to remote hosts. block (blackhole) drops traffic without a socket, and
                // dns-out is xray-internal — applying the binding to them produces
                // redundant fields in the generated config.
                foreach (var outbound in list.OfType<JsonObject>())
                {
                    var tag = outbound["tag"]?.GetValue<string>();
                    if (tag is ProxyOutboundTag or DirectOutboundTag or ChainEntryOutboundTag)
                        ApplyOutboundInterface(outbound, tunOutboundInterfaceName);
                }
            }

            return list;
        }

        private static (ServerEntry entryServer, ServerEntry exitServer) ResolveChainServers(
            ServerEntry chain,
            IEnumerable<ServerEntry>? availableServers)
        {
            if (availableServers is null)
            {
                throw new InvalidOperationException("链式代理需要服务器列表才能解析入口和出口节点。");
            }

            var servers = availableServers.ToList();
            var entryServer = servers.FirstOrDefault(server => server.Id == chain.ChainEntryServerId);
            var exitServer = servers.FirstOrDefault(server => server.Id == chain.ChainExitServerId);

            if (entryServer is null || exitServer is null)
            {
                throw new InvalidOperationException("链式代理引用的入口或出口节点不存在，请重新编辑该链式代理。");
            }

            if (entryServer.IsChain || exitServer.IsChain)
            {
                throw new InvalidOperationException("链式代理不能嵌套链式代理，请重新选择入口和出口节点。");
            }

            return (entryServer, exitServer);
        }

        private static void ApplyProxySettings(JsonObject outbound, string tag)
        {
            outbound["proxySettings"] = new JsonObject
            {
                ["tag"] = tag
            };
        }

        private static void ApplyOutboundInterface(JsonObject outbound, string interfaceName)
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
                    ["user"] = server.Username ?? string.Empty,
                    ["pass"] = server.Password ?? string.Empty,
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
                var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni;
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

        private static JsonObject BuildRouting(AppSettings settings)
        {
            var rules = new JsonArray();

            if (settings.IsTunMode)
            {
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
                    ["outboundTag"] = "direct",
                    ["process"] = CreateStringArray("self/", "xray/")
                });

                AddNode(rules, new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "block",
                    ["network"] = "udp",
                    ["port"] = "443"
                });
            }

            if (settings.RoutingMode == "global")
            {
                AddNode(rules, new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["network"] = "tcp,udp"
                });

                return new JsonObject
                {
                    ["domainStrategy"] = "AsIs",
                    ["rules"] = rules
                };
            }

            // User-defined custom rules run first (smart mode only, first-match-wins).
            if (settings.CustomRules is { } customRules)
            {
                foreach (var rule in customRules)
                {
                    if (!rule.IsEnabled || string.IsNullOrWhiteSpace(rule.Match))
                        continue;

                    var node = new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = rule.OutboundTag,
                    };
                    switch (rule.Type)
                    {
                        case "ip":      node["ip"]      = CreateStringArray(rule.Match); break;
                        case "process": node["process"] = CreateStringArray(rule.Match); break;
                        default:        node["domain"]  = CreateStringArray(rule.Match); break;
                    }

                    AddNode(rules, node);
                }
            }

            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "proxy",
                ["domain"] = CreateStringArray(
					"geosite:google"
				)
            });
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "direct",
                ["domain"] = CreateStringArray("geosite:cn", "geosite:private")
            });
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "direct",
                ["ip"] = CreateStringArray("geoip:cn", "geoip:private")
            });
            AddNode(rules, new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "proxy",
                ["network"] = "tcp,udp"
            });

            return new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = rules
            };
        }

        private static JsonObject BuildDns(AppSettings settings)
        {
            var directDns = settings.DirectDnsServer
                ?? (settings.IsTunMode ? "223.5.5.5" : "114.114.114.114");
            var proxyDns = settings.ProxyDnsServer ?? "8.8.8.8";

            var directEntry = new JsonObject
            {
                ["address"]      = directDns,
                ["domains"]      = CreateStringArray("geosite:cn", "geosite:private"),
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

        private static void AddNode(JsonArray array, JsonNode node)
        {
            array.Add(node);
        }

        private static void AddValue(JsonArray array, string value)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }
    }
}
