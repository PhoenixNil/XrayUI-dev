using System;
using System.Collections.Generic;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Resolves one release's feed entry to one language.
    /// Pure — no I/O, no dispatcher — so it is unit-tested directly.
    /// </summary>
    internal static class ChangelogSelector
    {
        /// <param name="target">
        /// The version actually being offered. Matching on it is not optional: the feed
        /// carries a release's notes *before* its tag is pushed (release.yml's
        /// changelog-gate requires that order), so the newest entry is regularly not the
        /// one being installed — a client still on an older build would otherwise be shown
        /// the upcoming version's notes under the current version's name. No match returns
        /// empty, and the dialog then drops the notes section entirely.
        /// </param>
        /// <param name="language">
        /// UI language code from the resources (<c>"zh"</c> / <c>"en"</c>). When the
        /// preferred language has no lines, the other one is used.
        /// </param>
        public static List<string> SelectForVersion(
            ChangelogFeed? feed, Version target, string? language)
        {
            if (feed?.Versions is null) return [];

            var preferZh = language is not null
                && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            foreach (var entry in feed.Versions)
            {
                if (entry is null) continue;
                if (!Version.TryParse(entry.Version, out var parsed)) continue;
                // Normalized: the feed writes "1.19", which parses with Build/Revision -1,
                // while a target built from "1.19.0" carries a zero Build.
                if (AppVersion.CompareNormalized(parsed, target) != 0) continue;

                var preferred = Clean(preferZh ? entry.Zh : entry.En);
                return preferred.Count > 0
                    ? preferred
                    : Clean(preferZh ? entry.En : entry.Zh);
            }

            return [];
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
