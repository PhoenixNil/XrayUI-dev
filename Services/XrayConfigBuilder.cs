using System.Linq;
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
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        public static string Build(ServerEntry server, AppSettings settings, string? outboundInterface = null)
        {
            var config = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["loglevel"] = "debug"
                },
                ["dns"] = BuildDns(settings),
                ["inbounds"] = BuildInbounds(settings),
                ["outbounds"] = BuildOutbounds(server, settings, outboundInterface),
                ["routing"] = BuildRouting(settings)
            };

            return config.ToJsonString(JsonOpts);
        }

        private static JsonArray BuildInbounds(AppSettings settings)
        {
            var list = new JsonArray();

            if (settings.IsTunMode)
            {
                AddNode(list, BuildTunInbound());
            }

            AddNode(list, new JsonObject
            {
                ["tag"] = "mixed-in",
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

        private static JsonObject BuildTunInbound()
        {
            return new JsonObject
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = "xray-tun",
                    ["address"] = "10.255.0.1/24",
                    ["mtu"] = 1500
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = CreateStringArray("http", "tls", "quic")
                }
            };
        }

        private static JsonArray BuildOutbounds(ServerEntry server, AppSettings settings, string? outboundInterface)
        {
            var proxy = BuildProxyOutbound(server);
            InjectSockopt(proxy, outboundInterface);

            var direct = new JsonObject
            {
                ["tag"] = "direct",
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject()
            };
            InjectSockopt(direct, outboundInterface);

            var list = new JsonArray();
            AddNode(list, proxy);
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

            return list;
        }

        private static void InjectSockopt(JsonObject outbound, string? iface)
        {
            if (string.IsNullOrWhiteSpace(iface))
            {
                return;
            }

            var sockopt = new JsonObject
            {
                ["interface"] = iface
            };

            if (outbound["streamSettings"] is JsonObject streamSettings)
            {
                streamSettings["sockopt"] = sockopt;
            }
            else
            {
                outbound["streamSettings"] = new JsonObject
                {
                    ["sockopt"] = sockopt
                };
            }
        }

        private static JsonObject BuildProxyOutbound(ServerEntry server)
        {
            return server.Protocol.ToLowerInvariant() switch
            {
                "vmess" => BuildVmessOutbound(server),
                "vless" => BuildVlessOutbound(server),
                "hysteria2" => BuildHysteria2Outbound(server),
                "trojan" => BuildTrojanOutbound(server),
                _ => BuildSsOutbound(server)
            };
        }

        private static JsonObject BuildSsOutbound(ServerEntry server)
        {
            var servers = new JsonArray();
            AddNode(servers, new JsonObject
            {
                ["address"] = server.Host,
                ["port"] = server.Port,
                ["method"] = server.Encryption,
                ["password"] = server.Password
            });

            return new JsonObject
            {
                ["tag"] = "proxy",
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
        }

        private static JsonObject BuildVmessOutbound(ServerEntry server)
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
                ["tag"] = "proxy",
                ["protocol"] = "vmess",
                ["settings"] = new JsonObject
                {
                    ["vnext"] = vnext
                },
                ["streamSettings"] = BuildStreamSettings(server)
            };
        }

        private static JsonObject BuildVlessOutbound(ServerEntry server)
        {
            var user = new JsonObject
            {
                ["id"] = server.Uuid,
                ["encryption"] = "none"
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
                ["tag"] = "proxy",
                ["protocol"] = "vless",
                ["settings"] = new JsonObject
                {
                    ["vnext"] = vnext
                },
                ["streamSettings"] = BuildStreamSettings(server)
            };
        }

        private static JsonObject BuildHysteria2Outbound(ServerEntry server)
        {
            var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni;

            return new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = "hysteria",
                ["settings"] = new JsonObject
                {
                    ["version"] = 2,
                    ["address"] = server.Host,
                    ["port"] = server.Port
                },
                ["streamSettings"] = new JsonObject
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
                }
            };
        }

        private static JsonObject BuildTrojanOutbound(ServerEntry server)
        {
            return new JsonObject
            {
                ["tag"] = "proxy",
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
                stream["tlsSettings"] = new JsonObject
                {
                    ["serverName"] = sni,
                    ["fingerprint"] = fingerprint,
                    ["allowInsecure"] = server.AllowInsecure
                };
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

            return stream;
        }

        private static JsonObject BuildRouting(AppSettings settings)
        {
            var rules = new JsonArray();

            if (settings.IsTunMode)
            {
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
                    if (rule.Type == "ip")
                        node["ip"] = CreateStringArray(rule.Match);
                    else
                        node["domain"] = CreateStringArray(rule.Match);

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
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            };
        }

        private static JsonObject BuildDns(AppSettings settings)
        {
            return settings.IsTunMode
                ? new JsonObject
                {
                    ["servers"] = CreateStringArray("223.5.5.5", "119.29.29.29", "localhost")
                }
                : new JsonObject
                {
                    ["servers"] = CreateStringArray("8.8.8.8", "114.114.114.114", "localhost")
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
