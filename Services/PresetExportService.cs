using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public class PresetExportService
    {
        private readonly SettingsService _settings;

        public PresetExportService(SettingsService settings)
        {
            _settings = settings;
        }

        public async Task<string> ExportAsync()
        {
            Directory.CreateDirectory(PresetPaths.Dir);

            var servers = await _settings.LoadServersAsync().ConfigureAwait(false);
            var settings = await _settings.LoadSettingsAsync().ConfigureAwait(false);

            var preset = new PresetSettings
            {
                Subscriptions = settings.Subscriptions is { Count: > 0 }
                    ? settings.Subscriptions.ToList()
                    : null,
                CustomRules = settings.CustomRules is { Count: > 0 }
                    ? settings.CustomRules.ToList()
                    : null,
            };

            var serversJson = JsonSerializer.Serialize(
                servers,
                AppJsonSerializerContext.Readable<List<ServerEntry>>());
            await File.WriteAllTextAsync(PresetPaths.ServersFile, serversJson).ConfigureAwait(false);

            var settingsJson = JsonSerializer.Serialize(
                preset,
                AppJsonSerializerContext.Readable<PresetSettings>());
            await File.WriteAllTextAsync(PresetPaths.SettingsFile, settingsJson).ConfigureAwait(false);

            return PresetPaths.Dir;
        }
    }
}
