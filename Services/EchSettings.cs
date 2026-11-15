namespace XrayUI.Services
{
    /// <summary>
    /// Normalization for the ECH force-query value share links carry. The core no longer has
    /// the option, so only the link parser and serializer use this, to round-trip the value.
    /// </summary>
    internal static class EchSettings
    {
        public const string Half = "half";
        public const string Full = "full";

        /// <summary>
        /// Returns "half"/"full" if value matches (after trim + lower-invariant); otherwise empty.
        /// "none" maps to empty since the model stores absence as empty string.
        /// </summary>
        public static string NormalizeForceQuery(string? value)
        {
            value = value?.Trim().ToLowerInvariant();
            return value is Half or Full ? value : string.Empty;
        }
    }
}
