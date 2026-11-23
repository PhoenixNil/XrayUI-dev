using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Parses proxy share links (ss://, vmess://, vless://, hysteria2://, trojan://, socks5://)
    /// into ServerEntry instances.
    /// AnyTLS is not supported by the current Xray-based client.
    /// Returns null on any parse failure.
    /// </summary>
    public static class NodeLinkParser
    {
        public static ServerEntry? Parse(string rawLink)
        {
            if (string.IsNullOrWhiteSpace(rawLink))
                return null;

            rawLink = rawLink.Trim().TrimStart('\uFEFF').Trim();

            if (rawLink.StartsWith("ss://",        StringComparison.OrdinalIgnoreCase)) return ParseSs(rawLink);
            if (rawLink.StartsWith("vmess://",     StringComparison.OrdinalIgnoreCase)) return ParseVmess(rawLink);
            if (rawLink.StartsWith("vless://",     StringComparison.OrdinalIgnoreCase)) return ParseVless(rawLink);
            if (rawLink.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(rawLink);
            if (rawLink.StartsWith("trojan://",    StringComparison.OrdinalIgnoreCase)) return ParseTrojan(rawLink);
            if (rawLink.StartsWith("socks5://",    StringComparison.OrdinalIgnoreCase)) return ParseSocks5(rawLink);

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

                // SS share links may carry a query (e.g. ?type=tcp from cross-protocol generators).
                // We ignore unknown params, but reject SIP003 plugin links — xray-core has no
                // SIP003 outbound, so silently dropping `plugin=...` would save an unconnectable node.
                var queryIdx = rest.IndexOf('?');
                if (queryIdx >= 0)
                {
                    var query = ParseQuery(rest.Substring(queryIdx));
                    if (query.ContainsKey("plugin"))
                        return null;
                    rest = rest.Substring(0, queryIdx);
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

                    // SIP002 permits one optional '/' after the port. Query and fragment
                    // have already been handled; leave credentials untouched and reject
                    // non-empty paths instead of silently discarding their contents.
                    var slashIdx = hostPart.IndexOf('/');
                    if (slashIdx >= 0)
                    {
                        if (slashIdx != hostPart.Length - 1)
                            return null;
                        hostPart = hostPart.Substring(0, slashIdx);
                    }

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
                var finalmask = FinalmaskJson.NormalizeForStorage(GetStr("fm", GetStr("finalmask")));

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
                    Finalmask   = finalmask,
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
                var vlessEncryption = Q(query, "encryption", string.Empty) ?? string.Empty;
                var echConfigList = Q(query, "echConfigList") ?? Q(query, "ech") ?? string.Empty;
                var echForceQuery = EchSettings.NormalizeForceQuery(
                    Q(query, "echForceQuery") ?? Q(query, "echfq"));
                var finalmask = FinalmaskJson.NormalizeForStorage(Q(query, "fm"));
                var allowInsecure = IsTruthy(Q(query, "allowInsecure")) || IsTruthy(Q(query, "insecure"));

                // Gated on xhttp: grpc links reuse the "mode" key for gun/multi. Case-insensitive
                // because network keeps the URI's original casing (only the builder lowercases).
                var xhttpMode  = string.Empty;
                var xhttpExtra = string.Empty;
                if (string.Equals(network, "xhttp", StringComparison.OrdinalIgnoreCase))
                {
                    xhttpMode  = XhttpSettings.NormalizeMode(Q(query, "mode"));
                    xhttpExtra = FinalmaskJson.NormalizeForStorage(Q(query, "extra"));
                }

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
                    EchConfigList = echConfigList,
                    EchForceQuery = echForceQuery,
                    PublicKey   = pk,
                    ShortId     = sid,
                    SpiderX     = spx,
                    Path        = path,
                    WsHost      = wsHost,
                    XhttpMode   = xhttpMode,
                    XhttpExtra  = xhttpExtra,
                    Flow        = flow,
                    VlessEncryption = vlessEncryption == "none" ? string.Empty : vlessEncryption,
                    Finalmask   = finalmask,
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
                var (normalizedLink, addressPortRange) = SplitHysteria2PortRange(link);

                var uri      = new Uri(normalizedLink);
                var password = Uri.UnescapeDataString(uri.UserInfo);
                var host     = uri.Host;
                var port     = uri.Port;
                var name     = string.IsNullOrEmpty(uri.Fragment)
                    ? string.Empty
                    : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

                var query = ParseQuery(uri.Query);
                var sni   = Q(query, "sni", string.Empty) ?? string.Empty;
                var finalmask = BuildHysteria2Finalmask(query, addressPortRange);
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
                    PinnedPeerCertSha256 =
                        (Q(query, "pinSHA256") ?? Q(query, "pinnedPeerCertSha256") ?? string.Empty).Trim(),
                    Finalmask  = finalmask,
                    Encryption = "TLS"
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// hysteria2's own URI form expresses port hopping as a range or list in the address
        /// ("host:35000-39000", "host:35000,36000"). System.Uri rejects a non-numeric port
        /// outright, so parsing the raw link threw and the node vanished at import. Rewrite the
        /// address down to its first port and hand the range back for the finalmask fold-in.
        /// </summary>
        private static (string link, string? portRange) SplitHysteria2PortRange(string link)
        {
            var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return (link, null);
            schemeEnd += 3;

            var authorityEnd = link.IndexOfAny(['/', '?', '#'], schemeEnd);
            if (authorityEnd < 0) authorityEnd = link.Length;

            // Auth may itself contain ':' and '@', so the host starts after the LAST '@'.
            var authority = link.Substring(schemeEnd, authorityEnd - schemeEnd);
            var hostStart = schemeEnd + authority.LastIndexOf('@') + 1;
            var hostPort  = link.Substring(hostStart, authorityEnd - hostStart);

            int colon;
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf(']');
                if (close < 0) return (link, null);
                colon = hostPort.IndexOf(':', close);
            }
            else
            {
                colon = hostPort.LastIndexOf(':');
            }
            if (colon < 0) return (link, null);

            var portPart = hostPort[(colon + 1)..];
            var separator = portPart.IndexOfAny(['-', ',']);
            if (separator < 0) return (link, null);

            var firstPort = portPart[..separator];
            if (!int.TryParse(firstPort, out _)) return (link, null);

            // The port runs to the end of the authority by construction, so the tail resumes at
            // authorityEnd — no need to re-derive it from the port length.
            var portStart = hostStart + colon + 1;
            return (string.Concat(link.AsSpan(0, portStart), firstPort, link.AsSpan(authorityEnd)), portPart);
        }

        private static string BuildHysteria2Finalmask(Dictionary<string, string> query, string? addressPortRange)
        {
            var finalmask = FinalmaskJson.NormalizeForStorage(
                Q(query, "fm") ?? Q(query, "finalmask"));

            var obfs = Q(query, "obfs");
            if (string.Equals(obfs, "salamander", StringComparison.OrdinalIgnoreCase))
            {
                var obfsPassword = Q(query, "obfs-password")
                                   ?? Q(query, "obfs_password")
                                   ?? Q(query, "obfsPassword");
                if (obfsPassword is not null)
                    finalmask = FinalmaskJson.AddHysteria2SalamanderMask(finalmask, obfsPassword);
            }

            // Query param wins over the address form — a client that emits both meant the param.
            var ports = Q(query, "mport") ?? Q(query, "ports") ?? addressPortRange;
            if (!string.IsNullOrWhiteSpace(ports))
                finalmask = FinalmaskJson.AddHysteria2UdpHop(finalmask, ports);

            return finalmask;
        }

        // Trojan

        private static ServerEntry? ParseTrojan(string link)
        {
            try
            {
                var uri      = new Uri(link);
                var password = Uri.UnescapeDataString(uri.UserInfo);
                if (string.IsNullOrEmpty(password)) return null;

                var host = uri.Host;
                var port = uri.Port > 0 ? uri.Port : 443;
                var name = string.IsNullOrEmpty(uri.Fragment)
                    ? string.Empty
                    : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

                var query = ParseQuery(uri.Query);
                var network = NormalizeTrojanNetwork(
                    Q(query, "type") ?? Q(query, "network") ?? "tcp");
                var security = Q(query, "security", "tls") ?? "tls";
                if (string.IsNullOrWhiteSpace(security))
                    security = "tls";
                security = security.ToLowerInvariant();

                var sni    = Q(query, "sni") ?? Q(query, "servername") ?? Q(query, "peer") ?? string.Empty;
                var fp     = Q(query, "fp", string.Empty) ?? string.Empty;
                var path   = Q(query, "path") ?? Q(query, "serviceName") ?? string.Empty;
                var wsHost = Q(query, "host", string.Empty) ?? string.Empty;
                var finalmask = FinalmaskJson.NormalizeForStorage(Q(query, "fm"));
                var allowInsecure = IsTruthy(Q(query, "allowInsecure")) || IsTruthy(Q(query, "insecure"));

                return new ServerEntry
                {
                    Name          = name,
                    Protocol      = "trojan",
                    Host          = host,
                    Port          = port,
                    Password      = password,
                    Network       = network,
                    Security      = security,
                    Sni           = sni,
                    Fingerprint   = fp,
                    AllowInsecure = allowInsecure,
                    Path          = path,
                    WsHost        = wsHost,
                    Finalmask     = finalmask,
                    Encryption    = security == "reality" ? "Reality"
                                  : security == "tls"     ? "TLS"
                                                          : "None"
                };
            }
            catch
            {
                return null;
            }
        }

        // ── SOCKS5 ────────────────────────────────────────────────────────────

        // Standard URIs only: socks5://[user[:pass]@]host:port[/][#name] with percent-encoded
        // credentials. Non-standard variants (Base64 userinfo, whole-payload Base64, query params,
        // a path) are rejected rather than imported with mangled credentials.
        private static ServerEntry? ParseSocks5(string link)
        {
            try
            {
                var rest = link.Substring("socks5://".Length);

                string name = string.Empty;
                var hashIdx = rest.IndexOf('#');
                if (hashIdx >= 0)
                {
                    name = Uri.UnescapeDataString(rest.Substring(hashIdx + 1));
                    rest = rest.Substring(0, hashIdx);
                }

                // RFC 3986: the authority ends at the first '/' or '?'. Only a lone trailing '/'
                // may follow it.
                if (rest.EndsWith('/'))
                    rest = rest.Substring(0, rest.Length - 1);
                if (rest.IndexOfAny(['/', '?']) >= 0)
                    return null;

                string username = string.Empty;
                string password = string.Empty;
                var atIdx = rest.LastIndexOf('@');
                if (atIdx >= 0)
                {
                    // Split on the raw ':' so a percent-encoded colon in the username survives.
                    var userinfo = rest.Substring(0, atIdx).Split(':', 2);
                    username = Uri.UnescapeDataString(userinfo[0]);
                    if (userinfo.Length > 1)
                        password = Uri.UnescapeDataString(userinfo[1]);

                    rest = rest.Substring(atIdx + 1);
                }

                if (!TrySplitStrictHostPort(rest, out var host, out var port))
                    return null;

                return new ServerEntry
                {
                    Name       = name,
                    Protocol   = "socks",
                    Host       = host,
                    Port       = port,
                    Username   = username,
                    Password   = password,
                    Network    = "tcp",
                    Security   = "none",
                    Encryption = string.Empty
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySplitStrictHostPort(string hostPort, out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            // "[ipv6]:port" or "dns-or-ipv4:port". An empty host or port fails the checks below.
            string hostText, portText;
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf("]:", StringComparison.Ordinal);
                if (close < 0)
                    return false;

                hostText = hostPort.Substring(1, close - 1);
                portText = hostPort.Substring(close + 2);
                if (Uri.CheckHostName(hostText) != UriHostNameType.IPv6)
                    return false;
            }
            else
            {
                var colonIdx = hostPort.LastIndexOf(':');
                if (colonIdx < 0)
                    return false;

                hostText = hostPort.Substring(0, colonIdx);
                portText = hostPort.Substring(colonIdx + 1);
                if (Uri.CheckHostName(hostText) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                    return false;
            }

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
                return false;

            host = hostText;
            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        internal static string? TryBase64Decode(string input)
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

        private static string NormalizeTrojanNetwork(string network)
        {
            if (string.IsNullOrWhiteSpace(network))
                return "tcp";

            network = network.ToLowerInvariant();
            return network == "original" ? "tcp" : network;
        }

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
