using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
                return new AppSettings();
            }
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, AppJsonSerializerContext.Default.AppSettings);
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
                return JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListServerEntry)
                       ?? new List<ServerEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load servers: {ex.Message}");
                return new List<ServerEntry>();
            }
        }

        public async Task SaveServersAsync(IEnumerable<ServerEntry> servers)
        {
            var serverList = servers as List<ServerEntry> ?? servers.ToList();
            var json = JsonSerializer.Serialize(serverList, AppJsonSerializerContext.Default.ListServerEntry);
            await File.WriteAllTextAsync(ServersFile, json).ConfigureAwait(false);
        }
    }
}
