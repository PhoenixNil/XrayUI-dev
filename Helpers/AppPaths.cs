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

        public static string SettingsJsonPath { get; } = Path.Combine(LocalAppDataDir, "settings.json");

        /// <summary>User-authored config profiles. Fixed file names because the slots are
        /// fixed — a user can drop their own file straight in here to replace one.</summary>
        public static string ProfilesDir { get; } = Path.Combine(LocalAppDataDir, "profiles");

        public static string TunProfilePath { get; } = Path.Combine(ProfilesDir, "tun.json");

        public static string ProxyProfilePath { get; } = Path.Combine(ProfilesDir, "proxy.json");

        /// <summary>The config XrayService writes on every start and hands to xray.exe.</summary>
        public static string XrayConfigPath { get; } = Path.Combine(LocalAppDataDir, "xray_config.json");

        /// <summary>A side-effect-free preview of the config that the currently selected node
        /// would use on its next start. Kept separate from XrayConfigPath so a preview can never
        /// be mistaken for, or overwrite, the config actually handed to xray.exe.</summary>
        public static string XrayConfigPreviewPath { get; } =
            Path.Combine(LocalAppDataDir, "xray_config.preview.json");
    }
}
