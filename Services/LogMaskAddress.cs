namespace XrayUI.Services
{
    /// <summary>
    /// Canonical names + validation for Xray's <c>log.maskAddress</c> setting,
    /// which controls IP redaction in xray's log output. Empty = feature off.
    /// </summary>
    internal static class LogMaskAddress
    {
        public const string Off = "";
        public const string Quarter = "quarter";
        public const string Half = "half";
        public const string Full = "full";

        /// <summary>True iff the value is one of the three xray-recognized levels.</summary>
        public static bool IsEnabled(string? value) =>
            value is Quarter or Half or Full;

        /// <summary>Returns the value if recognized, otherwise empty.</summary>
        public static string Normalize(string? value) =>
            value is Quarter or Half or Full ? value : Off;
    }
}
