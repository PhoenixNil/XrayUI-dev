using System;
using System.IO;

namespace XrayUI.Services
{
    internal static class PresetPaths
    {
        public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Import");
        public static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
        public static readonly string ServersFile = Path.Combine(Dir, "servers.json");
    }
}
