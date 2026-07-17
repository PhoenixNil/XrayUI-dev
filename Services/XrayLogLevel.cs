namespace XrayUI.Services
{
    /// <summary>
    /// Canonical names + validation for Xray's <c>log.loglevel</c> setting. Restricted to
    /// debug/info/warning: XrayReadySignal detects core startup via a Warning-level log line,
    /// which "error"/"none" would suppress, degrading every connect/switch/reapply to the
    /// 3s timeout fallback.
    /// </summary>
    internal static class XrayLogLevel
    {
        public const string Debug   = "debug";
        public const string Info    = "info";
        public const string Warning = "warning";

        /// <summary>Returns the value if recognized, otherwise the default (Warning).</summary>
        public static string Normalize(string? value) =>
            value is Debug or Info or Warning ? value : Warning;
    }
}
