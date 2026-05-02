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
    }
}
