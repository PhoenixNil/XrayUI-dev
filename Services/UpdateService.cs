using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
     /*<summary>
     Checks GitHub Releases for a newer XrayUI build, downloads +verifies the
     matching zip asset, extracts and validates it, then hands off to the
     standalone XrayUI.Updater.exe to overwrite the install directory.
     </summary> */
    public sealed class UpdateService : IUpdateService
    {
        private const string ReleaseApiUrl =
            "https://api.github.com/repos/PhoenixNil/XrayUI-dev/releases/latest";

        // User-facing release notes live on the website, not in the GitHub release body —
        // the release page stays a plain technical PR list, and the notes shown in-app can
        // be written (and translated) for end users.
        private const string ChangelogUrl = "https://www.xrayui.site/changelog.json";

        private const string AppExeName     = "XrayUI-dev.exe";
        private const string UpdaterExeName = "XrayUI.Updater.exe";

        // Which release-asset variant this build updates from. "-wasdk" zips bundle
        // the Windows App SDK runtime in the app folder; plain zips rely on the
        // machine-wide Windows App Runtime. The two must never cross-grade: the
        // updater only overwrites, so a wasdk install fed a plain zip keeps the old
        // runtime DLLs but gets an exe that ignores them and asks the bootstrapper
        // for an installed runtime — instant startup failure on machines without
        // one. WASDK_SELF_CONTAINED is defined in the csproj whenever the publish
        // runs with -p:WindowsAppSDKSelfContained=true (see release.yml).
#if WASDK_SELF_CONTAINED
        private const string AssetVariantSuffix = "-wasdk";
#else
        private const string AssetVariantSuffix = "";
#endif

        public async Task<UpdateInfo?> CheckAsync(string? proxyUrl, CancellationToken ct)
        {
            // Local builds carry the csproj default <Version>0.0.0-dev</Version>;
            // skip so dev iteration never tries to "upgrade" to the latest public release.
            if (AppVersion.IsDevBuild) return null;

            using var client = CreateGithubClient(proxyUrl, TimeSpan.FromSeconds(20));

            GhRelease? release;
            try
            {
                release = await client.GetFromJsonAsync(
                    ReleaseApiUrl, AppJsonSerializerContext.Default.GhRelease, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Network down, GitHub rate-limited, etc. — silent per failure policy.
                return null;
            }

            if (release is null || release.Draft || release.Prerelease) return null;

            var tag = (release.TagName ?? string.Empty).TrimStart('v');
            if (!Version.TryParse(tag, out var remoteVersion)) return null;
            if (AppVersion.CompareNormalized(remoteVersion, AppVersion.Current) <= 0) return null;

            var rid = CurrentRid();
            if (rid is null) return null;

            var zipName    = $"XrayUI-{rid}{AssetVariantSuffix}.zip";
            var sha256Name = $"{zipName}.sha256";

            string? zipUrl = null, shaUrl = null;
            if (release.Assets is not null)
            {
                foreach (var a in release.Assets)
                {
                    if (a?.Name is null || a.Url is null) continue;
                    if (a.Name == zipName)    zipUrl = a.Url;
                    if (a.Name == sha256Name) shaUrl = a.Url;
                }
            }

            // Both must be present. Missing zip = no asset for this arch/variant; missing
            // .sha256 = release mid-deploy or pre-feature build. Either way: silent skip.
            if (zipUrl is null || shaUrl is null) return null;

            return new UpdateInfo(remoteVersion, release.TagName!, zipUrl, shaUrl, zipName);
        }

        public async Task<IReadOnlyList<ChangelogEntry>> FetchChangelogAsync(
            UpdateInfo info, string? language, string? proxyUrl, CancellationToken ct)
        {
            try
            {
                using var client = CreateHttpClient(proxyUrl, TimeSpan.FromSeconds(10));

                // Cache-buster: an edge node still holding the previous file would otherwise
                // hide the notes for a release that just went out.
                var url = $"{ChangelogUrl}?v={info.NewVersion}";

                var feed = await client.GetFromJsonAsync(
                    url, AppJsonSerializerContext.Default.ChangelogFeed, ct);

                return ChangelogSelector.Select(feed, AppVersion.Current, info.NewVersion, language);
            }
            // Only a real caller cancellation propagates. HttpClient.Timeout also raises
            // OperationCanceledException (as TaskCanceledException) with ct untouched, and
            // rethrowing that would let a slow changelog host suppress the whole update
            // notification — the notes are decoration, they must never do that.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] Changelog fetch failed: {ex.Message}");
                return Array.Empty<ChangelogEntry>();
            }
        }

        public async Task<UpdateStaging> DownloadVerifyAndExtractAsync(
            UpdateInfo info, string? proxyUrl, IProgress<ProgressDialogUpdate> progress, CancellationToken ct)
        {
            var stageRoot   = Path.Combine(AppPaths.UpdatesDir, info.NewVersion.ToString());
            var downloadDir = Path.Combine(stageRoot, "download");
            var extractDir  = Path.Combine(stageRoot, "extracted");
            var runnerDir   = Path.Combine(stageRoot, "runner");

            // Always start from a clean stage dir — partial leftovers from a previous
            // failed attempt would otherwise confuse the version sanity check.
            if (Directory.Exists(stageRoot))
            {
                try { Directory.Delete(stageRoot, recursive: true); } catch { }
            }
            Directory.CreateDirectory(downloadDir);
            Directory.CreateDirectory(extractDir);
            Directory.CreateDirectory(runnerDir);

            using var client = CreateGithubClient(proxyUrl, TimeSpan.FromMinutes(10));

            // ── 1. .sha256 first (small, fail-fast on bad release) ─────────────────
            progress.Report(new ProgressDialogUpdate(Loc.GetString("Update_FetchingChecksum")));
            string expectedHash;
            try
            {
                var shaText = await client.GetStringAsync(info.Sha256Url, ct);
                expectedHash = ParseSha256SumLine(shaText)
                    ?? throw new InvalidDataException(Loc.GetString("Update_ChecksumFormatError"));
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException(Loc.GetString("Update_ChecksumDownloadFailed") + ex.Message);
            }

            // ── 2. zip — hash is computed during the streaming write so we don't ──
            //    need a second pass over the (~100MB+) file just to verify.
            var zipPath = Path.Combine(downloadDir, info.ZipAssetName);
            var actualHash = await DownloadAndHashAsync(
                client, info.ZipUrl, zipPath, info.ZipAssetName, progress, ct);

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.GetString("Update_ChecksumMismatch"));

            // ── 4. Extract ──────────────────────────────────────────────────────────
            progress.Report(new ProgressDialogUpdate(Loc.GetString("Update_Extracting")));
            try
            {
                await ZipFile.ExtractToDirectoryAsync(zipPath, extractDir, overwriteFiles: true, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(Loc.GetString("Update_ExtractFailed") + ex.Message);
            }

            // ── 5. Sanity check extracted contents ──────────────────────────────────
            progress.Report(new ProgressDialogUpdate(Loc.GetString("Update_Verifying")));

            var newAppExe     = Path.Combine(extractDir, AppExeName);
            var newUpdaterExe = Path.Combine(extractDir, UpdaterExeName);

            if (!File.Exists(newAppExe) || !File.Exists(newUpdaterExe))
                throw new InvalidDataException(Loc.GetString("Update_MissingFiles"));

            var actualFileVersion = FileVersionInfo.GetVersionInfo(newAppExe).FileVersion;
            if (string.IsNullOrEmpty(actualFileVersion) ||
                !Version.TryParse(actualFileVersion, out var parsedFv) ||
                AppVersion.CompareNormalized(parsedFv, info.NewVersion) != 0)
            {
                throw new InvalidDataException(
                    Loc.Format("Update_VersionMismatch", info.NewVersion, actualFileVersion));
            }

            // ── 6. Stage a runnable copy of the CURRENT updater ─────────────────────
            // We can't run the updater that's about to be overwritten — it must run
            // from a location outside the install dir. Since the *new* updater hasn't
            // been deployed yet, copy the currently-installed one.
            var installDir = Path.GetDirectoryName(Environment.ProcessPath)
                             ?? AppContext.BaseDirectory;
            var currentUpdater = Path.Combine(installDir, UpdaterExeName);
            if (!File.Exists(currentUpdater))
            {
                throw new FileNotFoundException(
                    Loc.GetString("Update_MissingUpdater"),
                    currentUpdater);
            }

            var stagedRunner = Path.Combine(runnerDir, UpdaterExeName);
            File.Copy(currentUpdater, stagedRunner, overwrite: true);

            progress.Report(new ProgressDialogUpdate(Loc.GetString("Update_PrepRestart")));

            return new UpdateStaging(extractDir, stagedRunner, installDir, info.NewVersion);
        }

        public void LaunchUpdater(UpdateStaging staging)
        {
            var psi = new ProcessStartInfo
            {
                FileName = staging.RunnerExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");
            psi.ArgumentList.Add($"--extracted-dir={staging.ExtractedDir}");
            psi.ArgumentList.Add($"--install-dir={staging.InstallDir}");
            psi.ArgumentList.Add($"--launch-after={AppExeName}");

            Process.Start(psi);
        }

        public void CleanupOldStagingDirs()
        {
            try
            {
                if (!Directory.Exists(AppPaths.UpdatesDir)) return;

                foreach (var sub in Directory.EnumerateDirectories(AppPaths.UpdatesDir))
                {
                    try
                    {
                        Directory.Delete(sub, recursive: true);
                    }
                    catch { /* locked runner or one bad dir shouldn't break the sweep */ }
                }
            }
            catch { /* best-effort */ }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string? CurrentRid() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "win-x64",
            Architecture.X86   => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };

        private static HttpClient CreateHttpClient(string? proxyUrl, TimeSpan timeout)
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                handler.Proxy    = new WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"XrayUI/{AppVersion.Current}");
            return client;
        }

        private static HttpClient CreateGithubClient(string? proxyUrl, TimeSpan timeout)
        {
            var client = CreateHttpClient(proxyUrl, timeout);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string? ParseSha256SumLine(string content)
        {
            var line = content.Trim();
            if (line.Length == 0) return null;

            int sep = 0;
            while (sep < line.Length && !char.IsWhiteSpace(line[sep])) sep++;
            var token = line[..sep];

            if (token.Length != 64) return null;
            foreach (var c in token)
            {
                if (!char.IsAsciiHexDigit(c)) return null;
            }
            return token.ToLowerInvariant();
        }

        private static async Task<string> DownloadAndHashAsync(
            HttpClient client, string url, string destPath, string displayName,
            IProgress<ProgressDialogUpdate> progress, CancellationToken ct)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            progress.Report(FormatProgress(displayName, 0, total));

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer      = new byte[81920];
            long received   = 0;
            long lastReport = 0;

            while (true)
            {
                var read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;

                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                hasher.AppendData(buffer, 0, read);
                received += read;

                if (received - lastReport >= 512 * 1024)
                {
                    progress.Report(FormatProgress(displayName, received, total));
                    lastReport = received;
                }
            }

            progress.Report(FormatProgress(displayName, received, total));
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        private static ProgressDialogUpdate FormatProgress(string name, long received, long? total)
        {
            var mbReceived = received / 1024.0 / 1024.0;
            if (total is > 0)
            {
                var mbTotal = total.Value / 1024.0 / 1024.0;
                var percent = received * 100.0 / total.Value;
                return new ProgressDialogUpdate(
                    Loc.Format("Update_Downloading", name, mbReceived, mbTotal),
                    percent);
            }

            return new ProgressDialogUpdate(Loc.Format("Update_DownloadingNoTotal", name, mbReceived));
        }
    }
}
