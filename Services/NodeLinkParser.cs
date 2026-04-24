using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Parses proxy share links (ss://, vmess://, vless://, hysteria2://) into ServerEntry instances.
    /// AnyTLS is not supported by the current Xray-based client.
    /// Returns null on any parse failure.
    /// </summary>
    public static class NodeLinkParser
    {
        public static ServerEntry? Parse(string rawLink)
        {
            if (string.IsNullOrWhiteSpace(rawLink))
                return null;

            rawLink = rawLink.Trim();

            if (rawLink.StartsWith("ss://",        StringComparison.OrdinalIgnoreCase)) return ParseSs(rawLink);
            if (rawLink.StartsWith("vmess://",     StringComparison.OrdinalIgnoreCase)) return ParseVmess(rawLink);
            if (rawLink.StartsWith("vless://",     StringComparison.OrdinalIgnoreCase)) return ParseVless(rawLink);
            if (rawLink.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(rawLink);

            return null;
        }

        // ── Shadowsocks ───────────────────────────────────────────────────────

        private static ServerEntry? ParseSs(string link)
        {
            try
            {
                // Strip scheme
                var rest = link.Substring("ss://".Length);

                // Extract fragment (name)
                string name = string.Empty;
                var hashIdx = rest.IndexOf('#');
                if (hashIdx >= 0)
                {
                    name = Uri.UnescapeDataString(rest.Substring(hashIdx + 1));
                    rest = rest.Substring(0, hashIdx);
                }

                // Try SIP002: BASE64(method:password)@host:port
                // Look for '@' that separates the base64 userinfo from host:port
                // The base64 part must NOT contain '@' — so find last '@' and try decoding left side
                string method, password, host;
                int port;

                int atIdx = rest.LastIndexOf('@');
                if (atIdx > 0)
                {
                    // SIP002 candidate
                    var userinfoPart = rest.Substring(0, atIdx);
                    var hostPart     = rest.Substring(atIdx + 1);

                    var decoded = TryBase64Decode(userinfoPart);
                    if (decoded != null && decoded.Contains(':'))
                    {
                        // Valid SIP002
                        var colonIdx = decoded.IndexOf(':');
                        method   = decoded.Substring(0, colonIdx);
                        password = decoded.Substring(colonIdx + 1);
                        (host, port) = SplitHostPort(hostPart);
                    }
                    else
                    {
                        // Raw SIP002: method:password@host:port — userinfo may be percent-encoded.
                        // SS2022 keys contain '+' '/' '=' which many generators escape as %2B %2F %3D.
                        var unescaped = Uri.UnescapeDataString(userinfoPart);
                        if (unescaped.Contains(':'))
                        {
                            var colonIdx = unescaped.IndexOf(':');
                            method   = unescaped.Substring(0, colonIdx);
                            password = unescaped.Substring(colonIdx + 1);
                            (host, port) = SplitHostPort(hostPart);
                        }
                        else
                        {
                            return ParseSsLegacy(rest, name);
                        }
                    }
                }
                else
                {
                    // Legacy: entire string is BASE64(method:password@host:port)
                    return ParseSsLegacy(rest, name);
                }

                return new ServerEntry
                {
                    Name       = name,
                    Protocol   = "ss",
                    Encryption = method,
                    Password   = password,
                    Host       = host,
                    Port       = port,
                    Network    = "tcp"
                };
            }
            catch
            {
                return null;
            }
        }

        private static ServerEntry? ParseSsLegacy(string base64Part, string name)
        {
            var decoded = TryBase64Decode(base64Part);
            if (decoded == null) return null;

            // format: method:password@host:port
            var atIdx = decoded.LastIndexOf('@');
            if (atIdx < 0) return null;

            var userinfo  = decoded.Substring(0, atIdx);
            var hostPart  = decoded.Substring(atIdx + 1);
            var colonIdx  = userinfo.IndexOf(':');
            if (colonIdx < 0) return null;

            var method   = userinfo.Substring(0, colonIdx);
            var password = userinfo.Substring(colonIdx + 1);
            var (host, port) = SplitHostPort(hostPart);

            return new ServerEntry
            {
                Name       = name,
                Protocol   = "ss",
                Encryption = method,
                Password   = password,
                Host       = host,
                Port       = port,
                Network    = "tcp"
            };
        }

        // ── VMess ─────────────────────────────────────────────────────────────

        private static ServerEntry? ParseVmess(string link)
        {
            try
            {
                var base64 = link.Substring("vmess://".Length);
                var json   = TryBase64Decode(base64);
                if (json == null) return null;

                using var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string GetStr(string key, string def = "") =>
                    root.TryGetProperty(key, out var v) ? v.GetString() ?? def : def;
                int GetInt(string key, int def = 0) =>
                    root.TryGetProperty(key, out var v)
                        ? (v.ValueKind == JsonValueKind.Number ? v.GetInt32()
                           : int.TryParse(v.GetString(), out int n) ? n : def)
                        : def;

                var net      = GetStr("net", "tcp");
                var tls      = GetStr("tls", "");
                var security = tls == "tls" ? "tls" : "none";
                var allowInsecure = IsTruthy(GetStr("allowInsecure")) || IsTruthy(GetStr("insecure"));

                return new ServerEntry
                {
                    Name        = GetStr("ps"),
                    Protocol    = "vmess",
                    Host        = GetStr("add"),
                    Port        = GetInt("port"),
                    Uuid        = GetStr("id"),
                    AlterId     = GetInt("aid"),
                    Network     = net,
                    Path        = GetStr("path"),
                    WsHost      = GetStr("host"),
                    Security    = security,
                    Sni         = GetStr("sni"),
                    Fingerprint = GetStr("fp"),
                    AllowInsecure = allowInsecure,
                    Encryption  = security == "tls" ? "TLS" : "None"
                };
            }
            catch
            {
                return null;
            }
        }

        // ── VLESS ─────────────────────────────────────────────────────────────

        private static ServerEntry? ParseVless(string link)
        {
            try
            {
                // vless://uuid@host:port?params#name
                var uri  = new Uri(link);
                var uuid = uri.UserInfo;
                var host = uri.Host;
                var port = uri.Port;
                var name = string.IsNullOrEmpty(uri.Fragment)
                    ? string.Empty
                    : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

                var query = ParseQuery(uri.Query);

                var network  = Q(query, "type",     "tcp") ?? "tcp";
                var security = Q(query, "security", "none") ?? "none";
                var sni      = Q(query, "sni") ?? Q(query, "servername") ?? string.Empty;
                var fp       = Q(query, "fp",   string.Empty) ?? string.Empty;
                var pk       = Q(query, "pbk",  string.Empty) ?? string.Empty;
                var sid      = Q(query, "sid",  string.Empty) ?? string.Empty;
                var spx      = Q(query, "spx",  string.Empty) ?? string.Empty;
                var path     = Q(query, "path") ?? Q(query, "serviceName") ?? string.Empty;
                var wsHost   = Q(query, "host", string.Empty) ?? string.Empty;
                var flow     = Q(query, "flow", string.Empty) ?? string.Empty;
                var allowInsecure = IsTruthy(Q(query, "allowInsecure")) || IsTruthy(Q(query, "insecure"));

                return new ServerEntry
                {
                    Name        = name,
                    Protocol    = "vless",
                    Host        = host,
                    Port        = port,
                    Uuid        = uuid,
                    Network     = network,
                    Security    = security,
                    Sni         = sni,
                    Fingerprint = fp,
                    AllowInsecure = allowInsecure,
                    PublicKey   = pk,
                    ShortId     = sid,
                    SpiderX     = spx,
                    Path        = path,
                    WsHost      = wsHost,
                    Flow        = flow,
                    Encryption  = security == "reality" ? "Reality"
                                : security == "tls"     ? "TLS"
                                                        : "None"
                };
            }
            catch
            {
                return null;
            }
        }

        // ── Hysteria2 ─────────────────────────────────────────────────────────

        private static ServerEntry? ParseHysteria2(string link)
        {
            try
            {
                var uri      = new Uri(link);
                var password = Uri.UnescapeDataString(uri.UserInfo);
                var host     = uri.Host;
                var port     = uri.Port;
                var name     = string.IsNullOrEmpty(uri.Fragment)
                    ? string.Empty
                    : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

                var query = ParseQuery(uri.Query);
                var sni   = Q(query, "sni", string.Empty) ?? string.Empty;
                var allowInsecure = IsTruthy(Q(query, "allowInsecure")) || IsTruthy(Q(query, "insecure"));

                return new ServerEntry
                {
                    Name       = name,
                    Protocol   = "hysteria2",
                    Host       = host,
                    Port       = port,
                    Password   = password,
                    Network    = "udp",
                    Security   = "tls",
                    Sni        = sni,
                    AllowInsecure = allowInsecure,
                    Encryption = "TLS"
                };
            }
            catch
            {
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string? TryBase64Decode(string input)
        {
            try
            {
                // Normalize URL-safe base64 and fix padding
                input = input.Replace('-', '+').Replace('_', '/');
                var pad = input.Length % 4;
                if (pad == 2)      input += "==";
                else if (pad == 3) input += "=";

                var bytes = Convert.FromBase64String(input);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        // ── Query string helpers ──────────────────────────────────────────────

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;

            var raw = query.TrimStart('?');
            foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = part.IndexOf('=');
                if (eqIdx < 0)
                {
                    d[Uri.UnescapeDataString(part)] = string.Empty;
                }
                else
                {
                    var key = Uri.UnescapeDataString(part.Substring(0, eqIdx));
                    var val = Uri.UnescapeDataString(part.Substring(eqIdx + 1));
                    d[key] = val;
                }
            }
            return d;
        }

        private static string? Q(Dictionary<string, string> d, string key, string? def = null)
            => d.TryGetValue(key, out var v) ? v : def;

        private static bool IsTruthy(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && (value == "1"
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

        private static (string host, int port) SplitHostPort(string hostPort)
        {
            // Handle IPv6 [::1]:port
            if (hostPort.StartsWith("["))
            {
                var close = hostPort.IndexOf(']');
                var h     = hostPort.Substring(1, close - 1);
                var p     = int.Parse(hostPort.Substring(close + 2));
                return (h, p);
            }

            var idx = hostPort.LastIndexOf(':');
            if (idx < 0) return (hostPort, 0);
            return (hostPort.Substring(0, idx), int.Parse(hostPort.Substring(idx + 1)));
        }
    }
}
