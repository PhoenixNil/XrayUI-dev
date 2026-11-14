using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    public class SettingsService
    {
        private static readonly string DataDir = AppPaths.LocalAppDataDir;

        private static readonly string SettingsFile = AppPaths.SettingsJsonPath;
        private static readonly string ServersFile  = Path.Combine(DataDir, "servers.json");

        private AppSettings? _cachedSettings;

        // Every write to settings.json / servers.json passes this gate, so a factory reset can
        // wait out a write already in flight before it seals the files.
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        // Set by ResetToDefaultsAsync and left set when it succeeds, because the caller restarts
        // straight afterwards. Any other write before exit would come from state loaded before
        // the reset (a held AppSettings instance, the live server list) and bring the wiped data
        // back. Only touched under _writeGate.
        private bool _writesSealed;

        public SettingsService()
        {
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>Drop the in-memory cache so the next LoadSettingsAsync re-reads the file.
        /// Used when an external process (e.g. the user's text editor) may have modified
        /// settings.json on disk.</summary>
        public void InvalidateCache() => _cachedSettings = null;

        /// <summary>Invalidate the cache and reload from disk in one call.</summary>
        public async Task<AppSettings> ReloadAsync()
        {
            InvalidateCache();
            return await LoadSettingsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Drop the cache and shell-open settings.json in the user's default .json editor.
        /// Cache is dropped first so subsequent reads pick up whatever the editor writes.
        /// Throws if the OS reports no association for .json.
        /// </summary>
        public void OpenInExternalEditor()
        {
            InvalidateCache();
            Process.Start(new ProcessStartInfo
            {
                FileName = SettingsFile,
                UseShellExecute = true,
            })?.Dispose();
        }

        /// <summary>
        /// Resets all application settings and server entries to defaults on disk,
        /// and deletes user-authored config profiles and generated xray configs.
        /// On success every later save is refused for the life of the process, so the
        /// caller must restart straight afterwards.
        /// </summary>
        public async Task ResetToDefaultsAsync()
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _writesSealed = true;

                // A failed deletion must reach the caller so it reports failure instead of
                // restarting into an apparently successful but incomplete factory reset.
                if (Directory.Exists(AppPaths.ProfilesDir))
                {
                    Directory.Delete(AppPaths.ProfilesDir, recursive: true);
                }
                File.Delete(AppPaths.XrayConfigPath);
                File.Delete(AppPaths.XrayConfigPreviewPath);
                File.Delete(AppPaths.XraySpeedtestConfigPath);
                // The taskbar's recent list itself is emptied by the startup refresh after the
                // restart, which prunes every ID the (now empty) server list no longer has.
                File.Delete(AppPaths.RecentConnectionsPath);

                var defaultSettings = new AppSettings
                {
                    RoutingRegion = InferDefaultRoutingRegion(),
                    SkipInitialImport = true,
                };
                _cachedSettings = defaultSettings;
                var settingsJson = JsonSerializer.Serialize(defaultSettings, AppJsonSerializerContext.Readable<AppSettings>());
                var serversJson = JsonSerializer.Serialize(new List<ServerEntry>(), AppJsonSerializerContext.Readable<List<ServerEntry>>());
                await AtomicFile.WriteAllTextAsync(SettingsFile, settingsJson).ConfigureAwait(false);
                await AtomicFile.WriteAllTextAsync(ServersFile, serversJson).ConfigureAwait(false);
            }
            catch
            {
                // No restart follows a failed reset, so the app has to be able to save again.
                _writesSealed = false;
                throw;
            }
            finally
            {
                _writeGate.Release();
            }
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
                    _cachedSettings = new AppSettings { RoutingRegion = InferDefaultRoutingRegion() };
                    return _cachedSettings;
                }

                var json = await File.ReadAllTextAsync(SettingsFile).ConfigureAwait(false);
                // A literal "null" body deserializes to null without throwing. Treat it as a
                // failed load rather than an empty one, or the next save writes defaults over it.
                var loaded = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppSettings)
                             ?? throw new JsonException("settings.json deserialized to null.");

                _cachedSettings = loaded;

                // One-time migration: settings.json written before XrayLogLevel existed only has
                // the legacy VerboseXrayLog bool. Populate the new field so every other reader
                // can trust it's non-null instead of re-deriving the fallback on every read.
                _cachedSettings.XrayLogLevel ??= _cachedSettings.VerboseXrayLog ? XrayLogLevel.Info : XrayLogLevel.Warning;

                return _cachedSettings;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Failed to load settings: {ex.Message}");
                // Deliberately not cached: the next call retries the file, so a hand-repair takes
                // effect without a restart. The instance is stamped so SaveSettingsAsync refuses
                // it no matter how much later the caller gets around to saving.
                return new AppSettings { IsFailedLoadFallback = true };
            }
        }

        /// <summary>
        /// First-run default for <see cref="AppSettings.RoutingRegion"/>. Deliberately narrow:
        /// only an explicit RU/IR Windows home region deviates from "cn" — geosite ships domestic
        /// lists for cn/ru/ir only, and system locale/region is a weak signal for everyone else
        /// (many zh users run en-US Windows or set region to US on VPS/VMs).
        /// </summary>
        private static string InferDefaultRoutingRegion()
        {
            try
            {
                var region = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion;
                if (string.Equals(region, "RU", StringComparison.OrdinalIgnoreCase)) return "ru";
                if (string.Equals(region, "IR", StringComparison.OrdinalIgnoreCase)) return "ir";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsService] Region inference failed: {ex.Message}");
            }
            return "cn";
        }

        /// <summary>
        /// Persists <paramref name="settings"/>. Returns false — without writing — when the
        /// instance came from a failed load (see <see cref="AppSettings.IsFailedLoadFallback"/>),
        /// or after a factory reset (see <see cref="ResetToDefaultsAsync"/>); callers that report
        /// success to the user, or act on the save having happened, must check it. Throws on I/O
        /// failure, as before.
        /// </summary>
        public async Task<bool> SaveSettingsAsync(AppSettings settings)
        {
            if (settings.IsFailedLoadFallback)
            {
                // Defaults handed out for an unreadable settings.json: writing them back is the
                // data loss, not the fix. Why the check rides the instance rather than a flag on
                // this service is argued in full on AppSettings.IsFailedLoadFallback.
                Debug.WriteLine("[SettingsService] Save refused: settings came from a failed load.");
                return false;
            }

            // Serialized before the gate so it still happens on the caller's thread, as before:
            // the instance is live state the UI thread keeps mutating.
            var json = JsonSerializer.Serialize(settings, AppJsonSerializerContext.Readable<AppSettings>());
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_writesSealed)
                {
                    Debug.WriteLine("[SettingsService] Save refused: a factory reset replaced the files.");
                    return false;
                }

                _cachedSettings = settings;
                await AtomicFile.WriteAllTextAsync(SettingsFile, json).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writeGate.Release();
            }
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
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_writesSealed)
                {
                    Debug.WriteLine("[SettingsService] Server save refused: a factory reset replaced the files.");
                    return;
                }

                await AtomicFile.WriteAllTextAsync(ServersFile, json).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }
}
