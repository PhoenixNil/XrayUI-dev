using System;
using System.Collections.Generic;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Picks the release notes to show for an upgrade: every version newer than the
    /// installed one up to and including the target, resolved to one language.
    /// Pure — no I/O, no dispatcher — so it is unit-tested directly.
    /// </summary>
    internal static class ChangelogSelector
    {
        /// <summary>
        /// Most version blocks to return, newest first. An install left stale for a
        /// long time would otherwise pile a dozen blocks into a small dialog.
        /// </summary>
        internal const int MaxVersions = 4;

        /// <param name="language">
        /// UI language code from the resources (<c>"zh"</c> / <c>"en"</c>). When the
        /// preferred language has no lines for a version, the other one is used —
        /// a half-translated feed still shows something rather than a blank gap.
        /// </param>
        public static List<ChangelogEntry> Select(
            ChangelogFeed? feed, Version currentVersion, Version targetVersion, string? language)
        {
            var result = new List<ChangelogEntry>();
            if (feed?.Versions is null) return result;

            var preferZh = language is not null
                && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            foreach (var version in feed.Versions)
            {
                if (version is null) continue;
                if (!Version.TryParse(version.Version, out var parsed)) continue;

                // Skip what the user already has, and anything beyond this upgrade —
                // the feed may already list versions newer than the target release.
                if (AppVersion.CompareNormalized(parsed, currentVersion) <= 0) continue;
                if (AppVersion.CompareNormalized(parsed, targetVersion) > 0) continue;

                var lines = PickLines(version, preferZh);
                if (lines.Count == 0) continue;

                result.Add(new ChangelogEntry(parsed, lines));
            }

            result.Sort((a, b) => AppVersion.CompareNormalized(b.Version, a.Version));   // newest first

            // Trim after sorting, so the cap keeps the newest versions rather than
            // whatever order the feed happened to list them in.
            if (result.Count > MaxVersions)
                result.RemoveRange(MaxVersions, result.Count - MaxVersions);

            return result;
        }

        private static List<string> PickLines(ChangelogVersion version, bool preferZh)
        {
            var preferred = Clean(preferZh ? version.Zh : version.En);
            return preferred.Count > 0
                ? preferred
                : Clean(preferZh ? version.En : version.Zh);
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
