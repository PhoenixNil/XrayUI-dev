using System;

namespace XrayUI.Services
{
    /// <summary>What the core refused to load, when its startup log names a cause we know.</summary>
    public enum CoreStartupProblem
    {
        /// <summary>Nothing recognized; the raw log is all there is to show.</summary>
        Unknown,
        /// <summary>VLESS or Trojan without transport security to a non-private address.</summary>
        PlaintextProxy,
        /// <summary>A Shadowsocks method the core no longer has. Detail is the method.</summary>
        UnknownCipher,
        /// <summary>A geosite:/geoip: category absent from the shipped .dat. Detail is the rule.</summary>
        MissingGeoCategory,
    }

    /// <summary>
    /// Reads the reason out of an xray "Failed to start" line. The core stays the only judge of
    /// what loads: nothing here predicts a rejection, it only recognizes one after the fact, so
    /// the checks cannot drift from the core's own (which address counts as private, which
    /// categories a .dat holds). An unrecognized log simply yields <see cref="CoreStartupProblem.Unknown"/>.
    /// </summary>
    /// <param name="Problem">The recognized cause.</param>
    /// <param name="OutboundTag">The outbound the core was building when it gave up, or null
    /// when the failure is not tied to one (routing, DNS) or the outbound had no tag.</param>
    /// <param name="Detail">The offending value for causes that have one, else null.</param>
    public readonly record struct CoreStartupDiagnosis(
        CoreStartupProblem Problem, string? OutboundTag, string? Detail)
    {
        // Verbatim fragments of the core's messages (infra/conf, common/geodata).
        private const string OutboundTagMarker = "failed to build outbound config with tag ";
        private const string PlaintextMarker = "is prohibited unless the server address is a private IP or domain";
        private const string CipherMarker = "unknown cipher method:";
        private const string GeoCodeMarker = "failed to check code ";
        private const string DomainRuleMarker = "illegal domain rule: ";
        private const string IpRuleMarker = "illegal ip rule: ";
        private const string Separator = " > ";

        public static CoreStartupDiagnosis Parse(string? startupLog)
        {
            if (string.IsNullOrEmpty(startupLog))
                return default;

            var tag = ValueAfter(startupLog, OutboundTagMarker);

            if (startupLog.Contains(PlaintextMarker, StringComparison.Ordinal))
                return new(CoreStartupProblem.PlaintextProxy, tag, null);

            if (startupLog.Contains(CipherMarker, StringComparison.Ordinal))
                return new(CoreStartupProblem.UnknownCipher, tag, ValueAfter(startupLog, CipherMarker) ?? string.Empty);

            if (startupLog.Contains(GeoCodeMarker, StringComparison.Ordinal))
            {
                var rule = ValueAfter(startupLog, DomainRuleMarker) ?? ValueAfter(startupLog, IpRuleMarker);
                if (rule is not null)
                    return new(CoreStartupProblem.MissingGeoCategory, tag, rule);
            }

            return new(CoreStartupProblem.Unknown, tag, null);
        }

        /// <summary>
        /// The text between <paramref name="marker"/> and the next " > " link of the core's
        /// error chain (or the end of that line), trimmed. Null when the marker is absent or
        /// nothing follows it — an untagged outbound prints "with tag  > ...".
        /// </summary>
        private static string? ValueAfter(string log, string marker)
        {
            int start = log.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += marker.Length;

            int end = log.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
                end = log.Length;

            int separator = log.IndexOf(Separator, start, end - start, StringComparison.Ordinal);
            if (separator >= 0)
                end = separator;

            var value = log[start..end].Trim();
            return value.Length > 0 ? value : null;
        }
    }
}
