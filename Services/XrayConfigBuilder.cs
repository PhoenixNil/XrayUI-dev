using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Builds an xray-core JSON configuration string for the given server and app settings.
    /// </summary>
    public static class XrayConfigBuilder
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <param name="outboundInterface">
        /// TUN 模式下检测到的出站物理网卡名称（如 "WLAN" 或 "以太网"）。
        /// 非 TUN 模式传 null，此时 sockopt 不注入。
        /// </param>
        public static string Build(ServerEntry server, AppSettings settings, string? outboundInterface = null)
        {
            var config = new Dictionary<string, object>
            {
                ["log"] = new { loglevel = "debug" },
                ["dns"] = BuildDns(settings),
                ["inbounds"] = BuildInbounds(settings),
                ["outbounds"] = BuildOutbounds(server, settings, outboundInterface),
                ["routing"] = BuildRouting(settings)
            };

            return JsonSerializer.Serialize(config, JsonOpts);
        }

        // ── Inbounds ──────────────────────────────────────────────────────────

        private static object[] BuildInbounds(AppSettings settings)
        {
            var list = new List<object>
            {
                new
                {
                    tag = "socks-in",
                    protocol = "socks",
                    listen = "127.0.0.1",
                    port = settings.LocalSocksPort,
                    settings = new { auth = "noauth", udp = true }
                },
                new
                {
                    tag = "http-in",
                    protocol = "http",
                    listen = "127.0.0.1",
                    port = settings.LocalHttpPort,
                    settings = new { }
                }
            };

            if (settings.IsTunMode)
                list.Insert(0, BuildTunInbound());

            return list.ToArray();
        }

        private static object BuildTunInbound() => new
        {
            tag = "tun-in",
            protocol = "tun",
            settings = new
            {
                // name 决定 wintun 接口名称，必须与 TunService.DefaultTunInterfaceName 一致
                name = "xray-tun",
                address = "10.255.0.1/24",
                mtu = 1500
            },
            sniffing = new
            {
                enabled = true,
                destOverride = new[] { "http", "tls", "quic" }
            }
        };

        // ── Outbounds ─────────────────────────────────────────────────────────

        private static object[] BuildOutbounds(ServerEntry server, AppSettings settings, string? outboundInterface)
        {
            var proxy = BuildProxyOutbound(server);
            InjectSockopt(proxy, outboundInterface);

            var direct = new Dictionary<string, object?>
            {
                ["tag"] = "direct",
                ["protocol"] = "freedom",
                ["settings"] = new { }
            };
            InjectSockopt(direct, outboundInterface);

            var list = new List<object> { proxy, direct };

            // TUN 模式需要 blackhole 出站来承接 QUIC 阻断规则
            if (settings.IsTunMode)
                list.Add(new { tag = "block", protocol = "blackhole", settings = new { } });

            return list.ToArray();
        }

        /// <summary>
        /// 向出站的 streamSettings 注入 sockopt.interface，防止出站流量回流进 TUN 接口。
        /// iface 为 null 时不注入（非 TUN 模式）。
        /// </summary>
        private static void InjectSockopt(Dictionary<string, object?> outbound, string? iface)
        {
            if (iface == null) return;

            var sockopt = new Dictionary<string, object?> { ["interface"] = iface };

            if (outbound.TryGetValue("streamSettings", out var ss) && ss is Dictionary<string, object?> streamDict)
            {
                streamDict["sockopt"] = sockopt;
            }
            else
            {
                outbound["streamSettings"] = new Dictionary<string, object?> { ["sockopt"] = sockopt };
            }
        }

        private static Dictionary<string, object?> BuildProxyOutbound(ServerEntry server) =>
            server.Protocol.ToLowerInvariant() switch
            {
                "vmess" => BuildVmessOutbound(server),
                "vless" => BuildVlessOutbound(server),
                "hysteria2" => BuildHysteria2Outbound(server),
                _ => BuildSsOutbound(server)
            };

        private static Dictionary<string, object?> BuildSsOutbound(ServerEntry s) => new()
        {
            ["tag"] = "proxy",
            ["protocol"] = "shadowsocks",
            ["settings"] = new
            {
                servers = new[]
                {
                    new { address = s.Host, port = s.Port, method = s.Encryption, password = s.Password }
                }
            },
            ["streamSettings"] = new Dictionary<string, object?> { ["network"] = "tcp" }
        };

        private static Dictionary<string, object?> BuildVmessOutbound(ServerEntry s)
        {
            var streamSettings = BuildStreamSettings(s);
            return new()
            {
                ["tag"] = "proxy",
                ["protocol"] = "vmess",
                ["settings"] = new
                {
                    vnext = new[]
                    {
                        new
                        {
                            address = s.Host,
                            port = s.Port,
                            users = new[]
                            {
                                new { id = s.Uuid, alterId = s.AlterId, security = "auto" }
                            }
                        }
                    }
                },
                ["streamSettings"] = streamSettings
            };
        }

        private static Dictionary<string, object?> BuildVlessOutbound(ServerEntry s)
        {
            var streamSettings = BuildStreamSettings(s);

            var flowValue = !string.IsNullOrWhiteSpace(s.Flow) ? s.Flow : null;

            var userDict = new Dictionary<string, object?> { ["id"] = s.Uuid, ["encryption"] = "none" };
            if (flowValue != null)
                userDict["flow"] = flowValue;

            return new()
            {
                ["tag"] = "proxy",
                ["protocol"] = "vless",
                ["settings"] = new
                {
                    vnext = new[]
                    {
                        new
                        {
                            address = s.Host,
                            port = s.Port,
                            users = new[] { userDict }
                        }
                    }
                },
                ["streamSettings"] = streamSettings
            };
        }

        private static Dictionary<string, object?> BuildHysteria2Outbound(ServerEntry s)
        {
            var sni = string.IsNullOrEmpty(s.Sni) ? s.Host : s.Sni;
            return new()
            {
                ["tag"] = "proxy",
                ["protocol"] = "hysteria",
                ["settings"] = new
                {
                    version = 2,
                    address = s.Host,
                    port = s.Port
                },
                ["streamSettings"] = new Dictionary<string, object?>
                {
                    ["network"] = "hysteria",
                    ["security"] = "tls",
                    ["tlsSettings"] = new { serverName = sni, allowInsecure = s.AllowInsecure },
                    ["hysteriaSettings"] = new { version = 2, auth = s.Password }
                }
            };
        }

        private static Dictionary<string, object?> BuildStreamSettings(ServerEntry s)
        {
            var network = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network.ToLowerInvariant();
            var security = string.IsNullOrEmpty(s.Security) ? "none" : s.Security.ToLowerInvariant();

            object? tlsSettings = null;
            object? realitySettings = null;

            if (security == "tls")
            {
                var sni = string.IsNullOrEmpty(s.Sni) ? s.Host : s.Sni;
                var fp = string.IsNullOrEmpty(s.Fingerprint) ? "chrome" : s.Fingerprint;
                tlsSettings = new { serverName = sni, fingerprint = fp, allowInsecure = s.AllowInsecure };
            }
            else if (security == "reality")
            {
                var sni = string.IsNullOrEmpty(s.Sni) ? s.Host : s.Sni;
                var fp = string.IsNullOrEmpty(s.Fingerprint) ? "chrome" : s.Fingerprint;
                var spx = string.IsNullOrEmpty(s.SpiderX) ? "/" : s.SpiderX;
                realitySettings = new
                {
                    serverName = sni,
                    fingerprint = fp,
                    publicKey = s.PublicKey,
                    shortId = s.ShortId,
                    spiderX = spx
                };
                security = "reality";
            }

            if (network == "ws")
            {
                var headers = string.IsNullOrEmpty(s.WsHost)
                    ? (object)new { }
                    : new { Host = s.WsHost };

                return BuildStreamObject(network, security, tlsSettings, realitySettings,
                    wsSettings: new { path = s.Path, headers });
            }

            if (network == "grpc")
            {
                return BuildStreamObject(network, security, tlsSettings, realitySettings,
                    grpcSettings: new { serviceName = s.Path });
            }

            return BuildStreamObject(network, security, tlsSettings, realitySettings);
        }

        private static Dictionary<string, object?> BuildStreamObject(
            string network,
            string security,
            object? tlsSettings,
            object? realitySettings,
            object? wsSettings = null,
            object? grpcSettings = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["network"] = network,
                ["security"] = security
            };

            if (tlsSettings != null)   d["tlsSettings"]    = tlsSettings;
            if (realitySettings != null) d["realitySettings"] = realitySettings;
            if (wsSettings != null)    d["wsSettings"]     = wsSettings;
            if (grpcSettings != null)  d["grpcSettings"]   = grpcSettings;

            return d;
        }

        // ── Routing ───────────────────────────────────────────────────────────

        private static object BuildRouting(AppSettings settings)
        {
            var rules = new List<object>();

            // TUN 模式：首条规则阻断 UDP/443（QUIC），防止浏览器绕过代理
            if (settings.IsTunMode)
            {
                rules.Add(new { type = "field", outboundTag = "block", network = "udp", port = "443" });
            }

            if (settings.RoutingMode == "global")
            {
                rules.Add(new { type = "field", outboundTag = "proxy", network = "tcp,udp" });

                return new
                {
                    domainStrategy = "AsIs",
                    rules = rules.ToArray()
                };
            }

            rules.Add(new { type = "field", outboundTag = "direct", domain = new[] { "geosite:cn", "geosite:private" } });
            rules.Add(new { type = "field", outboundTag = "direct", ip = new[] { "geoip:cn", "geoip:private" } });
            rules.Add(new { type = "field", outboundTag = "proxy", network = "tcp,udp" });

            return new
            {
                domainStrategy = "IPIfNonMatch",
                rules = rules.ToArray()
            };
        }

        // ── DNS ───────────────────────────────────────────────────────────────

        private static object BuildDns(AppSettings settings)
        {
            // TUN 模式下使用国内 DNS，防止 DNS 泄漏到隧道外
            return settings.IsTunMode
                ? new { servers = new[] { "223.5.5.5", "119.29.29.29", "localhost" } }
                : new { servers = new[] { "8.8.8.8", "114.114.114.114", "localhost" } };
        }
    }
}
