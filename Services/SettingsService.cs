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

        private AppSettings? _cachedSettings;

        public SettingsService()
        {
            Directory.CreateDirectory(DataDir);
        }

        // ── AppSettings ───────────────────────────────────────────────────────

        public async Task<AppSettings> LoadSettingsAsync()
        {
            if (_cachedSettings is not null)
                return _cachedSettings;

            try
            {
                if (!File.Exists(SettingsFile))
                {
                    _cachedSettings = new AppSettings();
                    return _cachedSettings;
                }

                var json = await File.ReadAllTextAsync(SettingsFile).ConfigureAwait(false);
                _cachedSettings = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings) ?? new AppSettings();
                return _cachedSettings;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
                return new AppSettings();
            }
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            _cachedSettings = settings;
            var json = JsonSerializer.Serialize(settings, AppJsonSerializerContext.Readable<AppSettings>());
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
                var list = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListServerEntry)
                           ?? [];

                // Persist once if legacy JSON has no Id keys, so field-initializer-generated
                // Ids don't regenerate on every launch and break LastAutoConnectServerId.
                if (list.Count > 0 && !json.Contains("\"Id\":", StringComparison.Ordinal))
                    await SaveServersAsync(list).ConfigureAwait(false);

                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load servers: {ex.Message}");
                return [];
            }
        }

        public async Task SaveServersAsync(IEnumerable<ServerEntry> servers)
        {
            var serverList = servers as List<ServerEntry> ?? servers.ToList();
            var json = JsonSerializer.Serialize(serverList, AppJsonSerializerContext.Readable<List<ServerEntry>>());
            await File.WriteAllTextAsync(ServersFile, json).ConfigureAwait(false);
        }
    }
}
