using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public class InitialImportService
    {
        private readonly SettingsService _settings;

        public InitialImportService(SettingsService settings)
        {
            _settings = settings;
        }

        public async Task ImportAsync()
        {
            if (!Directory.Exists(PresetPaths.Dir))
                return;

            try
            {
                await TryImportServersAsync().ConfigureAwait(false);
                await TryImportSettingsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitialImport] Import failed: {ex}");
            }
        }

        private async Task TryImportServersAsync()
        {
            if (!File.Exists(PresetPaths.ServersFile))
                return;

            var existing = await _settings.LoadServersAsync().ConfigureAwait(false);
            if (existing.Count > 0)
                return;

            var preset = await ReadJsonAsync(
                PresetPaths.ServersFile,
                AppJsonSerializerContext.Default.ListServerEntry,
                static () => new List<ServerEntry>()).ConfigureAwait(false);
            if (preset.Count == 0)
                return;

            await _settings.SaveServersAsync(preset).ConfigureAwait(false);
            Debug.WriteLine($"[InitialImport] Imported {preset.Count} servers.");
        }

        private async Task TryImportSettingsAsync()
        {
            if (!File.Exists(PresetPaths.SettingsFile))
                return;

            var preset = await ReadJsonAsync(
                PresetPaths.SettingsFile,
                AppJsonSerializerContext.Default.PresetSettings,
                static () => new PresetSettings()).ConfigureAwait(false);

            var hasSubscriptions = preset.Subscriptions is { Count: > 0 };
            var hasRules = preset.CustomRules is { Count: > 0 };
            if (!hasSubscriptions && !hasRules)
                return;

            var target = await _settings.LoadSettingsAsync().ConfigureAwait(false);
            var changed = false;

            if (hasSubscriptions && (target.Subscriptions?.Count ?? 0) == 0)
            {
                target.Subscriptions = preset.Subscriptions!.ToList();
                changed = true;
            }

            if (hasRules && (target.CustomRules?.Count ?? 0) == 0)
            {
                target.CustomRules = preset.CustomRules!.ToList();
                changed = true;
            }

            if (!changed)
                return;

            await _settings.SaveSettingsAsync(target).ConfigureAwait(false);
            Debug.WriteLine("[InitialImport] Imported subscriptions/custom rules.");
        }

        private static async Task<T> ReadJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback)
        {
            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                return JsonSerializer.Deserialize(json, typeInfo) ?? fallback();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitialImport] Failed to read {path}: {ex.Message}");
                return fallback();
            }
        }
    }
}
