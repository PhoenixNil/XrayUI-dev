using System;
using System.Text;
using System.Text.Json;
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

            var obj = new
            {
                v             = "2",
                ps            = s.Name ?? string.Empty,
                add           = s.Host ?? string.Empty,
                port          = s.Port,
                id            = s.Uuid ?? string.Empty,
                aid           = s.AlterId,
                net           = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network,
                type          = "none",
                host          = s.WsHost ?? string.Empty,
                path          = s.Path   ?? string.Empty,
                tls           = tls,
                sni           = s.Sni    ?? string.Empty,
                fp            = s.Fingerprint ?? string.Empty,
                // IsTruthy("1") = true, IsTruthy("") = false — clean round-trip
                allowInsecure = s.AllowInsecure ? "1" : ""
            };

            var json   = JsonSerializer.Serialize(obj);
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

            // Reality-only params
            if (security == "reality")
            {
                AppendIfNotEmpty(sb, "pbk", s.PublicKey);
                AppendIfNotEmpty(sb, "sid", s.ShortId);
                AppendIfNotEmpty(sb, "spx", s.SpiderX);
            }

            // Transport params
            if (network == "ws" || network == "grpc")
                AppendIfNotEmpty(sb, "path", s.Path);

            if (network == "ws")
                AppendIfNotEmpty(sb, "host", s.WsHost);

            // VLESS flow (xtls-rprx-vision etc.)
            AppendIfNotEmpty(sb, "flow", s.Flow);

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

            bool hasQuery = !string.IsNullOrEmpty(s.Sni) || s.AllowInsecure;
            if (hasQuery)
            {
                sb.Append('?');
                bool first = true;
                if (!string.IsNullOrEmpty(s.Sni))
                {
                    AppendParam(sb, "sni", s.Sni, first: true);
                    first = false;
                }
                if (s.AllowInsecure)
                    AppendParam(sb, "allowInsecure", "1", first: first);
            }

            if (!string.IsNullOrEmpty(s.Name))
            {
                sb.Append('#');
                sb.Append(Uri.EscapeDataString(s.Name));
            }

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Wraps bare IPv6 addresses (containing ':') in brackets for URI use.</summary>
        private static string FormatHost(string? host)
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
