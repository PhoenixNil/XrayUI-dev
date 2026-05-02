using System;
using System.IO;

namespace XrayUI.Helpers
{
    public static class AppPaths
    {
        public static string LocalAppDataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XrayUI");

        public static string UpdatesDir { get; } = Path.Combine(LocalAppDataDir, "Updates");
    }
}
