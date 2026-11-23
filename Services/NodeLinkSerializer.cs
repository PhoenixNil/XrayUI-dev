using System;
using System.Text;
using System.Text.Json.Nodes;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Produces standard share links from a ServerEntry.
    /// Exact inverse of NodeLinkParser — formats are kept in sync.
    /// Returns null for unsupported protocols or entries missing required fields.
    /// </summary>
    public static class NodeLinkSerializer
    {
        public static string? ToLink(ServerEntry server)
        {
            if (server is null) return null;

            return (server.Protocol?.ToLowerInvariant()) switch
            {
                "ss"        => ToSsLink(server),
                "vmess"     => ToVmessLink(server),
                "vless"     => ToVlessLink(server),
                "hysteria2" => ToHysteria2Link(server),
                "trojan"    => ToTrojanLink(server),
                "socks"     => ToSocks5Link(server),
                _           => null
            };
        }

        // ── Shadowsocks (SIP002) ──────────────────────────────────────────────

        private static string? ToSsLink(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.Encryption) || string.IsNullOrEmpty(s.Password))
                return null;

            // Standard (padded) Base64 of "method:password"
            var userinfo = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{s.Encryption}:{s.Password}"));

            var host = FormatHost(s.Host);
            var fragment = string.IsNullOrEmpty(s.Name)
                ? string.Empty
                : $"#{Uri.EscapeDataString(s.Name)}";

            return $"ss://{userinfo}@{host}:{s.Port}{fragment}";
        }

        // ── VMess ─────────────────────────────────────────────────────────────

        private static string? ToVmessLink(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.Uuid)) return null;

            // "tls" when security == tls, else empty — mirrors ParseVmess reading
            var tls = s.Security?.ToLowerInvariant() == "tls" ? "tls" : "";

            var payload = new JsonObject
            {
                ["v"] = "2",
                ["ps"] = s.Name ?? string.Empty,
                ["add"] = s.Host ?? string.Empty,
                ["port"] = s.Port,
                ["id"] = s.Uuid ?? string.Empty,
                ["aid"] = s.AlterId,
                ["net"] = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network,
                ["type"] = "none",
                ["host"] = s.WsHost ?? string.Empty,
                ["path"] = s.Path ?? string.Empty,
                ["tls"] = tls,
                ["sni"] = s.Sni ?? string.Empty,
                ["fp"] = s.Fingerprint ?? string.Empty,
                ["fm"] = FinalmaskJson.NormalizeForShare(s.Finalmask),
                // IsTruthy("1") = true, IsTruthy("") = false — clean round-trip
                ["allowInsecure"] = s.AllowInsecure ? "1" : ""
            };

            var json   = payload.ToJsonString();
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return $"vmess://{base64}";
        }

        // ── VLESS ─────────────────────────────────────────────────────────────

        private static string? ToVlessLink(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.Uuid)) return null;

            var host     = FormatHost(s.Host);
            var security = string.IsNullOrEmpty(s.Security) ? "none" : s.Security.ToLowerInvariant();
            var network  = string.IsNullOrEmpty(s.Network)  ? "tcp"  : s.Network.ToLowerInvariant();

            var sb = new StringBuilder();
            sb.Append("vless://");
            sb.Append(Uri.EscapeDataString(s.Uuid));
            sb.Append('@');
            sb.Append(host);
            sb.Append(':');
            sb.Append(s.Port);
            sb.Append('?');

            // Always-present base params
            AppendParam(sb, "type",     network,  first: true);
            AppendParam(sb, "security", security, first: false);

            // TLS / Reality shared params
            if (security == "tls" || security == "reality")
            {
                AppendIfNotEmpty(sb, "sni", s.Sni);
                AppendIfNotEmpty(sb, "fp",  s.Fingerprint);
                if (s.AllowInsecure)
                    AppendParam(sb, "allowInsecure", "1", first: false);
            }

            if (security == "tls" && !string.IsNullOrWhiteSpace(s.EchConfigList))
            {
                AppendIfNotEmpty(sb, "echConfigList", s.EchConfigList);
                AppendIfNotEmpty(sb, "echForceQuery", EchSettings.NormalizeForceQuery(s.EchForceQuery));
            }

            // Reality-only params
            if (security == "reality")
            {
                AppendIfNotEmpty(sb, "pbk", s.PublicKey);
                AppendIfNotEmpty(sb, "sid", s.ShortId);
                AppendIfNotEmpty(sb, "spx", s.SpiderX);
            }

            // Transport params
            if (network == "ws" || network == "grpc" || network == "xhttp")
                AppendIfNotEmpty(sb, "path", s.Path);

            if (network == "ws" || network == "xhttp")
                AppendIfNotEmpty(sb, "host", s.WsHost);

            if (network == "xhttp")
            {
                AppendIfNotEmpty(sb, "mode", XhttpSettings.NormalizeMode(s.XhttpMode));
                AppendIfNotEmpty(sb, "extra", FinalmaskJson.NormalizeForShare(s.XhttpExtra));
            }

            // VLESS-level encryption (Xray PR #5067). Always emit, matching v2rayN parity & spec.
            var vlessEnc = string.IsNullOrEmpty(s.VlessEncryption) ? "none" : s.VlessEncryption;
            AppendParam(sb, "encryption", vlessEnc, first: false);

            // VLESS flow (xtls-rprx-vision etc.)
            AppendIfNotEmpty(sb, "flow", s.Flow);
            AppendIfNotEmpty(sb, "fm", FinalmaskJson.NormalizeForShare(s.Finalmask));

            // Fragment
            if (!string.IsNullOrEmpty(s.Name))
            {
                sb.Append('#');
                sb.Append(Uri.EscapeDataString(s.Name));
            }

            return sb.ToString();
        }

        // ── Hysteria2 ─────────────────────────────────────────────────────────

        private static string? ToHysteria2Link(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.Password)) return null;

            var host     = FormatHost(s.Host);
            var password = Uri.EscapeDataString(s.Password);

            var sb = new StringBuilder();
            sb.Append("hysteria2://");
            sb.Append(password);
            sb.Append('@');
            sb.Append(host);
            sb.Append(':');
            sb.Append(s.Port);

            // Everything the mask can express as a dedicated link parameter is lifted out by
            // FinalmaskJson; whatever is left rides along in fm=. This method only maps the
            // results onto query keys.
            var (hasSalamanderObfs, obfsPassword, mask) =
                FinalmaskJson.TakeHysteria2Salamander(FinalmaskJson.NormalizeForShare(s.Finalmask));
            var (hopPorts, finalmask) = FinalmaskJson.TakeHysteria2UdpHop(mask);

            bool hasQuery = !string.IsNullOrEmpty(s.Sni)
                            || s.AllowInsecure
                            || !string.IsNullOrEmpty(s.PinnedPeerCertSha256)
                            || hasSalamanderObfs
                            || hopPorts is not null
                            || !string.IsNullOrEmpty(finalmask);
            if (hasQuery)
            {
                sb.Append('?');
                bool first = true;

                void AddParam(string key, string value)
                {
                    AppendParam(sb, key, value, first);
                    first = false;
                }

                if (!string.IsNullOrEmpty(s.Sni))
                    AddParam("sni", s.Sni);
                if (s.AllowInsecure)
                {
                    AddParam("insecure", "1");
                }
                if (!string.IsNullOrEmpty(s.PinnedPeerCertSha256))
                {
                    AddParam("pinSHA256", s.PinnedPeerCertSha256);
                }
                if (hasSalamanderObfs)
                {
                    AddParam("obfs", "salamander");
                    AddParam("obfs-password", obfsPassword ?? string.Empty);
                }
                if (hopPorts is not null)
                {
                    AddParam("mport", hopPorts);
                }
                if (!string.IsNullOrEmpty(finalmask))
                {
                    AddParam("fm", finalmask);
                }
            }

            if (!string.IsNullOrEmpty(s.Name))
            {
                sb.Append('#');
                sb.Append(Uri.EscapeDataString(s.Name));
            }

            return sb.ToString();
        }

        // ── Trojan ───────────────────────────────────────────────────────────

        private static string? ToTrojanLink(ServerEntry s)
        {
            if (string.IsNullOrEmpty(s.Password)) return null;

            var host = FormatHost(s.Host);
            var password = Uri.EscapeDataString(s.Password);
            var network = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network.ToLowerInvariant();
            var security = string.IsNullOrEmpty(s.Security) ? "tls" : s.Security.ToLowerInvariant();

            var sb = new StringBuilder();
            sb.Append("trojan://");
            sb.Append(password);
            sb.Append('@');
            sb.Append(host);
            sb.Append(':');
            sb.Append(s.Port > 0 ? s.Port : 443);

            var finalmask = FinalmaskJson.NormalizeForShare(s.Finalmask);
            bool hasQuery = network != "tcp"
                            || security != "tls"
                            || !string.IsNullOrEmpty(s.Sni)
                            || !string.IsNullOrEmpty(s.Fingerprint)
                            || s.AllowInsecure
                            || !string.IsNullOrEmpty(s.Path)
                            || !string.IsNullOrEmpty(s.WsHost)
                            || !string.IsNullOrEmpty(finalmask);
            if (hasQuery)
            {
                sb.Append('?');
                bool first = true;

                void AddParam(string key, string value)
                {
                    AppendParam(sb, key, value, first);
                    first = false;
                }

                void AddIfNotEmpty(string key, string? value)
                {
                    if (!string.IsNullOrEmpty(value))
                        AddParam(key, value);
                }

                if (network != "tcp")
                    AddParam("type", network);
                if (security != "tls")
                    AddParam("security", security);
                AddIfNotEmpty("sni", s.Sni);
                AddIfNotEmpty("fp", s.Fingerprint);
                if (s.AllowInsecure)
                    AddParam("allowInsecure", "1");

                if (network == "grpc")
                    AddIfNotEmpty("serviceName", s.Path);
                else
                    AddIfNotEmpty("path", s.Path);

                if (network == "ws")
                    AddIfNotEmpty("host", s.WsHost);
                AddIfNotEmpty("fm", finalmask);
            }

            if (!string.IsNullOrEmpty(s.Name))
            {
                sb.Append('#');
                sb.Append(Uri.EscapeDataString(s.Name));
            }

            return sb.ToString();
        }

        // ── SOCKS5 ────────────────────────────────────────────────────────────

        private static string? ToSocks5Link(ServerEntry s)
        {
            if (string.IsNullOrWhiteSpace(s.Host) || s.Port is < 1 or > 65535)
                return null;

            var sb = new StringBuilder();
            sb.Append("socks5://");

            if (!string.IsNullOrEmpty(s.Username) || !string.IsNullOrEmpty(s.Password))
            {
                sb.Append(Uri.EscapeDataString(s.Username ?? string.Empty));
                sb.Append(':');
                sb.Append(Uri.EscapeDataString(s.Password ?? string.Empty));
                sb.Append('@');
            }

            sb.Append(FormatHost(s.Host));
            sb.Append(':');
            sb.Append(s.Port);

            if (!string.IsNullOrEmpty(s.Name))
            {
                sb.Append('#');
                sb.Append(Uri.EscapeDataString(s.Name));
            }

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Wraps bare IPv6 addresses (containing ':') in brackets for URI use.</summary>
        internal static string FormatHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) return string.Empty;
            return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        }

        private static void AppendParam(StringBuilder sb, string key, string value, bool first)
        {
            if (!first) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
        }

        private static void AppendIfNotEmpty(StringBuilder sb, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append('&');
                sb.Append(Uri.EscapeDataString(key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(value));
            }
        }
    }
}
