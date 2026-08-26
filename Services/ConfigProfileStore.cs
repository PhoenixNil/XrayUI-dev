using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Reads and writes the two hand-written config profiles under
    /// <c>%LocalAppData%\XrayUI\profiles\</c>. Two fixed slots, two fixed file names — the
    /// user can drop their own config straight into the folder to replace one.
    ///
    /// The store deliberately does no validation: it is the transport, and
    /// <see cref="ConfigProfileJson.Validate"/> is the gate. The editor validates before
    /// writing, and XrayConfigBuilder validates again on the way in, which is what catches a
    /// file hand-edited outside the app.
    /// </summary>
    public sealed class ConfigProfileStore
    {
        /// <summary>Every slot's live path, so the bundling helpers below iterate the files
        /// themselves rather than a slot flag they have to decode back into a path.</summary>
        private static readonly string[] LiveProfilePaths =
            [AppPaths.ProxyProfilePath, AppPaths.TunProfilePath];

        /// <summary>The same profile's path inside a preset folder.</summary>
        private static string InDir(string dir, string livePath) =>
            Path.Combine(dir, Path.GetFileName(livePath));

        public static string PathFor(bool tunSlot) =>
            tunSlot ? AppPaths.TunProfilePath : AppPaths.ProxyProfilePath;

        /// <summary>The raw profile text, or null when the slot has no file yet.</summary>
        public async Task<string?> ReadAsync(bool tunSlot)
        {
            var path = PathFor(tunSlot);
            if (!File.Exists(path)) return null;

            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }

        public async Task WriteAsync(bool tunSlot, string json)
        {
            Directory.CreateDirectory(AppPaths.ProfilesDir);
            await AtomicFile.WriteAllTextAsync(PathFor(tunSlot), json).ConfigureAwait(false);
        }

        /// <summary>
        /// The profile that governs the next start, or null to use the generated config. The
        /// slot is chosen by TUN mode rather than by what the files contain, so a start that
        /// already elevated for TUN cannot end up running the system-proxy profile.
        /// </summary>
        public async Task<string?> LoadActiveAsync(AppSettings settings, bool tunMode)
        {
            var enabled = tunMode ? settings.UseTunConfigProfile : settings.UseProxyConfigProfile;
            if (!enabled) return null;

            // Deliberately not ReadAsync: a missing file here means the user enabled a profile
            // and then deleted it out of the folder. Falling back to the generated config would
            // start something they did not ask for while the UI still says "custom".
            var path = PathFor(tunMode);
            if (!File.Exists(path))
                throw new FileNotFoundException(Loc.Format("Error_ConfigProfileMissingMsg", path), path);

            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }

        /// <summary>
        /// Mirrors the saved profiles into <paramref name="destDir"/> for a distribution bundle,
        /// and deletes any stale copy of a slot the user no longer has — an export has to
        /// describe the current state, not accumulate what it described last time.
        /// Returns how many profiles the bundle ended up carrying.
        /// </summary>
        public static async Task<int> CopyToAsync(string destDir)
        {
            var copied = 0;

            foreach (var source in LiveProfilePaths)
            {
                var target = InDir(destDir, source);

                if (File.Exists(source))
                {
                    await CopyAsync(source, target).ConfigureAwait(false);
                    copied++;
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            return copied;
        }

        /// <summary>
        /// Copies profiles out of a preset folder into the live one.
        ///
        /// Never deletes: the two enable switches live in settings.json and deliberately do not
        /// travel with a preset, so a slot the recipient already turned on has to keep the file
        /// it points at. Clearing it here would leave the switch on with nothing behind it, which
        /// is the one state <see cref="LoadActiveAsync"/> refuses to start.
        /// </summary>
        /// <param name="overwrite">False fills only the slots with no file yet (first-launch
        /// import); true replaces them (explicit restore).</param>
        public static async Task<int> CopyFromAsync(string sourceDir, bool overwrite)
        {
            if (!Directory.Exists(sourceDir)) return 0;

            var copied = 0;

            foreach (var target in LiveProfilePaths)
            {
                var source = InDir(sourceDir, target);

                if (!File.Exists(source)) continue;
                if (!overwrite && File.Exists(target)) continue;

                await CopyAsync(source, target).ConfigureAwait(false);
                copied++;
            }

            return copied;
        }

        /// <summary>True when a preset folder carries at least one profile.</summary>
        public static bool HasProfilesIn(string sourceDir) =>
            Directory.Exists(sourceDir)
            && LiveProfilePaths.Any(live => File.Exists(InDir(sourceDir, live)));

        private static async Task CopyAsync(string source, string target)
        {
            var text = await File.ReadAllTextAsync(source).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await AtomicFile.WriteAllTextAsync(target, text).ConfigureAwait(false);
        }

        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ProfilesDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.ProfilesDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigProfileStore] OpenFolder failed: {ex.Message}");
            }
        }
    }
}
