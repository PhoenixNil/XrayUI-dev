using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public sealed class PresetImportService
    {
        private readonly SettingsService _settings;

        public PresetImportService(SettingsService settings)
        {
            _settings = settings;
        }

        public static bool PresetExists() =>
            File.Exists(PresetPaths.ServersFile) || File.Exists(PresetPaths.SettingsFile);

        public async Task<PresetImportResult> ApplyAsync()
        {
            var importedServers = await TryReplaceServersAsync().ConfigureAwait(false);
            var settingsResult = await TryReplaceSettingsAsync().ConfigureAwait(false);

            return new PresetImportResult(
                importedServers,
                settingsResult.Subscriptions,
                settingsResult.CustomRules,
                settingsResult.AdvancedRouting);
        }

        private async Task<int> TryReplaceServersAsync()
        {
            if (!File.Exists(PresetPaths.ServersFile))
                return 0;

            var preset = await PresetReader.ReadRequiredJsonAsync(
                PresetPaths.ServersFile,
                AppJsonSerializerContext.Default.ListServerEntry,
                "PresetImport").ConfigureAwait(false);

            await _settings.SaveServersAsync(preset).ConfigureAwait(false);
            return preset.Count;
        }

        // Replace semantics: a missing field in the preset clears the existing value
        // (in contrast to InitialImportService's merge-if-empty behavior).
        private async Task<(int Subscriptions, int CustomRules, bool AdvancedRouting)> TryReplaceSettingsAsync()
        {
            if (!File.Exists(PresetPaths.SettingsFile))
                return (0, 0, false);

            var preset = await PresetReader.ReadRequiredJsonAsync(
                PresetPaths.SettingsFile,
                AppJsonSerializerContext.Default.PresetSettings,
                "PresetImport").ConfigureAwait(false);

            var target = await _settings.LoadSettingsAsync().ConfigureAwait(false);

            if (target.IsFailedLoadFallback)
            {
                // Restoring a preset means "reset to the distributed state", and an unreadable
                // settings.json is precisely the situation it is meant to rescue — so this is the
                // one path that must stay able to write. The instance handed back by a failed load
                // is refused by SaveSettingsAsync, so rebuild on clean defaults instead: whatever
                // the broken file held could not be read back anyway, and silently reporting a
                // successful import that wrote nothing is the worse outcome.
                target = new AppSettings();
            }

            target.Subscriptions = preset.Subscriptions is { Count: > 0 }
                ? preset.Subscriptions
                    .Select(subscription => subscription.ToSubscription())
                    .ToList()
                : null;

            target.CustomRules = preset.CustomRules is { Count: > 0 }
                ? preset.CustomRules.ToList()
                : null;

            target.AdvancedRouting = preset.AdvancedRouting?.DeepClone() as JsonObject;

            await _settings.SaveSettingsAsync(target).ConfigureAwait(false);

            return (
                target.Subscriptions?.Count ?? 0,
                target.CustomRules?.Count ?? 0,
                target.AdvancedRouting is not null);
        }
    }

    public sealed record PresetImportResult(
        int ImportedServers,
        int ImportedSubscriptions,
        int ImportedCustomRules,
        bool ImportedAdvancedRouting);
}
