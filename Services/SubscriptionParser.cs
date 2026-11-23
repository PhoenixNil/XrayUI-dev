using System;
using System.Collections.Generic;
using System.Text;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Decodes a subscription response body (standard/URL-safe/unpadded Base64 or plain text)
    /// and parses it as a newline-delimited share-link list, falling back to Clash / Clash.Meta
    /// YAML when no line parses as a share link.
    /// </summary>
    public static class SubscriptionParser
    {
        public static List<ServerEntry> Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return [];

            var text = (TryDecodeBase64Payload(raw) ?? raw).TrimStart('\uFEFF');

            var entries = new List<ServerEntry>();
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = NodeLinkParser.Parse(line.Trim());
                if (entry != null)
                    entries.Add(entry);
            }

            // Fall back to Clash YAML only when no line parsed as a share link, so a normal link
            // list never pays for a YAML parse. The order is safe for Clash configs because no
            // supported link scheme (deliberately no http://) matches one of their lines, such as
            // a health-check URL folded onto its own line.
            if (entries.Count == 0)
            {
                try
                {
                    entries.AddRange(ClashConfigParser.Parse(text).Nodes);
                }
                catch
                {
                    // Not valid YAML either — caller treats an empty list as "nothing parsed".
                }
            }

            return entries;
        }

        private static string? TryDecodeBase64Payload(string raw)
        {
            var trimmed = raw.Trim().TrimStart('\uFEFF');

            // Drop whitespace (MIME line wrapping) and bail out at the first character outside the
            // standard/URL-safe Base64 alphabet: plain link lists and YAML always contain ':', so
            // they exit here without a decode attempt. TryBase64Decode maps the URL-safe alphabet,
            // restores missing padding and decodes UTF-8 leniently, so one bad byte in a display
            // name doesn't discard the whole feed.
            var sb = new StringBuilder();
            foreach (var c in trimmed)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=' or '-' or '_'))
                    return null;

                sb.Append(c);
            }

            if (sb.Length == 0)
                return null;

            var decoded = NodeLinkParser.TryBase64Decode(sb.ToString());
            return decoded is not null && decoded.IndexOf('\0') < 0 ? decoded : null;
        }
    }
}
