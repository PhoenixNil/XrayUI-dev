using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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
                await TryImportProfilesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitialImport] Import failed: {ex}");
            }
        }

        /// <summary>
        /// Fills only the slots with no profile yet, matching how servers and settings are
        /// treated here. The enable switches are not part of the preset, so the files land
        /// inert and the user turns them on after reading them.
        /// </summary>
        private static async Task TryImportProfilesAsync()
        {
            var copied = await ConfigProfileStore
                .CopyFromAsync(PresetPaths.ProfilesDir, overwrite: false)
                .ConfigureAwait(false);

            if (copied > 0)
                Debug.WriteLine($"[InitialImport] Imported {copied} config profiles.");
        }

        private async Task TryImportServersAsync()
        {
            if (!File.Exists(PresetPaths.ServersFile))
                return;

            var existing = await _settings.LoadServersAsync().ConfigureAwait(false);
            if (existing.Count > 0)
                return;

            var preset = await PresetReader.ReadJsonAsync(
                PresetPaths.ServersFile,
                AppJsonSerializerContext.Default.ListServerEntry,
                static () => new List<ServerEntry>(),
                "InitialImport").ConfigureAwait(false);
            if (preset.Count == 0)
                return;

            await _settings.SaveServersAsync(preset).ConfigureAwait(false);
            Debug.WriteLine($"[InitialImport] Imported {preset.Count} servers.");
        }

        private async Task TryImportSettingsAsync()
        {
            if (!File.Exists(PresetPaths.SettingsFile))
                return;

            var preset = await PresetReader.ReadJsonAsync(
                PresetPaths.SettingsFile,
                AppJsonSerializerContext.Default.PresetSettings,
                static () => new PresetSettings(),
                "InitialImport").ConfigureAwait(false);

            var hasSubscriptions = preset.Subscriptions is { Count: > 0 };
            var hasRules = preset.CustomRules is { Count: > 0 };
            var hasAdvancedRouting = preset.AdvancedRouting is not null;
            if (!hasSubscriptions && !hasRules && !hasAdvancedRouting)
                return;

            var target = await _settings.LoadSettingsAsync().ConfigureAwait(false);
            var changed = false;

            if (hasSubscriptions && (target.Subscriptions?.Count ?? 0) == 0)
            {
                target.Subscriptions = preset.Subscriptions!
                    .Select(subscription => subscription.ToSubscription())
                    .ToList();
                changed = true;
            }

            if (hasRules && (target.CustomRules?.Count ?? 0) == 0)
            {
                target.CustomRules = preset.CustomRules!.ToList();
                changed = true;
            }

            if (hasAdvancedRouting && target.AdvancedRouting is null)
            {
                target.AdvancedRouting = preset.AdvancedRouting!.DeepClone() as JsonObject;
                changed = true;
            }

            if (!changed)
                return;

            await _settings.SaveSettingsAsync(target).ConfigureAwait(false);
            Debug.WriteLine("[InitialImport] Imported subscriptions/custom rules/advanced routing.");
        }
    }
}
