using System;
using System.Collections.Generic;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Resolves the feed's first (latest) release to one language.
    /// Pure — no I/O, no dispatcher — so it is unit-tested directly.
    /// </summary>
    internal static class ChangelogSelector
    {
        /// <param name="language">
        /// UI language code from the resources (<c>"zh"</c> / <c>"en"</c>). When the
        /// preferred language has no lines, the other one is used.
        /// </param>
        public static List<string> SelectLatest(ChangelogFeed? feed, string? language)
        {
            if (feed?.Versions is not { Count: > 0 }) return [];

            var preferZh = language is not null
                && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            var latest = feed.Versions[0];
            var preferred = Clean(preferZh ? latest.Zh : latest.En);
            return preferred.Count > 0
                ? preferred
                : Clean(preferZh ? latest.En : latest.Zh);
        }

        private static List<string> Clean(List<string>? lines)
        {
            var result = new List<string>();
            if (lines is null) return result;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                result.Add(line.Trim());
            }
            return result;
        }
    }
}
