using System;

namespace XrayUI.Helpers
{
    public static class AppVersion
    {
        public static Version Current { get; } =
            typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);

        // Local Debug builds inherit the csproj default <Version>0.0.0-dev</Version>,
        // which Assembly.Version surfaces as 0.0.0.0. Skip update checks for those
        // so dev iteration never tries to "upgrade" to the latest public release.
        public static bool IsDevBuild =>
            Current.Major == 0 && Current.Minor == 0 && Current.Build == 0;

        /// <summary>
        /// Orders two versions with absent components treated as zero. System.Version
        /// reports missing parts as -1, so a feed's "1.18" would otherwise compare
        /// below an installed "1.18.0" — every version comparison in the update flow
        /// goes through here so they all agree.
        /// </summary>
        public static int CompareNormalized(Version a, Version b) =>
            Normalize(a).CompareTo(Normalize(b));

        private static (int, int, int, int) Normalize(Version v) =>
            (v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
