using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public class SettingsService
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XrayUI");

        private static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
        private static readonly string ServersFile  = Path.Combine(DataDir, "servers.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented          = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public SettingsService()
        {
            Directory.CreateDirectory(DataDir);
        }

        // ── AppSettings ───────────────────────────────────────────────────────

        public async Task<AppSettings> LoadSettingsAsync()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new AppSettings();

                var json = await File.ReadAllTextAsync(SettingsFile).ConfigureAwait(false);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            await File.WriteAllTextAsync(SettingsFile, json).ConfigureAwait(false);
        }

        // ── Server list ───────────────────────────────────────────────────────

        public async Task<List<ServerEntry>> LoadServersAsync()
        {
            try
            {
                if (!File.Exists(ServersFile))
                    return new List<ServerEntry>();

                var json = await File.ReadAllTextAsync(ServersFile).ConfigureAwait(false);
                return JsonSerializer.Deserialize<List<ServerEntry>>(json, JsonOpts)
                       ?? new List<ServerEntry>();
            }
            catch
            {
                return new List<ServerEntry>();
            }
        }

        public async Task SaveServersAsync(IEnumerable<ServerEntry> servers)
        {
            var json = JsonSerializer.Serialize(servers, JsonOpts);
            await File.WriteAllTextAsync(ServersFile, json).ConfigureAwait(false);
        }
    }
}
