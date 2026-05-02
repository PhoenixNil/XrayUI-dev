using System;
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
    /// <summary>
    /// Checks GitHub Releases for a newer XrayUI build, downloads + verifies the
    /// matching zip asset, extracts and validates it, then hands off to the
    /// standalone XrayUI.Updater.exe to overwrite the install directory.
    ///
    /// HTTP / SHA256 / progress patterns are intentionally cloned from
    /// <see cref="GeoDataUpdateService"/> rather than refactored into a shared
    /// helper — they are ~50 LOC each and the two services have different
    /// failure-policy requirements.
    /// </summary>
    public sealed class UpdateService : IUpdateService
    {
        private const string ReleaseApiUrl =
            "https://api.github.com/repos/PhoenixNil/XrayUI-dev/releases/latest";

        private const string AppExeName     = "XrayUI-dev.exe";
        private const string UpdaterExeName = "XrayUI.Updater.exe";

        public async Task<UpdateInfo?> CheckAsync(string? proxyUrl, CancellationToken ct)
        {
            // Local builds carry the csproj default <Version>0.0.0-dev</Version>;
            // skip so dev iteration never tries to "upgrade" to the latest public release.
            if (AppVersion.IsDevBuild) return null;

            using var client = CreateHttpClient(proxyUrl, TimeSpan.FromSeconds(20));

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
            if (remoteVersion <= AppVersion.Current) return null;

            var rid = CurrentRid();
            if (rid is null) return null;

            var zipName    = $"XrayUI-{release.TagName}-{rid}.zip";
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

            // Both must be present. Missing zip = no asset for this arch; missing
            // .sha256 = release mid-deploy or pre-feature build. Either way: silent skip.
            if (zipUrl is null || shaUrl is null) return null;

            return new UpdateInfo(remoteVersion, release.TagName!, zipUrl, shaUrl, zipName);
        }

        public async Task<UpdateStaging> DownloadVerifyAndExtractAsync(
            UpdateInfo info, string? proxyUrl, IProgress<string> progress, CancellationToken ct)
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

            using var client = CreateHttpClient(proxyUrl, TimeSpan.FromMinutes(10));

            // ── 1. .sha256 first (small, fail-fast on bad release) ─────────────────
            progress.Report("正在获取校验文件…");
            string expectedHash;
            try
            {
                var shaText = await client.GetStringAsync(info.Sha256Url, ct);
                expectedHash = ParseSha256SumLine(shaText)
                    ?? throw new InvalidDataException("更新校验文件格式异常");
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法下载更新校验文件：" + ex.Message);
            }

            // ── 2. zip — hash is computed during the streaming write so we don't ──
            //    need a second pass over the (~100MB+) file just to verify.
            var zipPath = Path.Combine(downloadDir, info.ZipAssetName);
            var actualHash = await DownloadAndHashAsync(
                client, info.ZipUrl, zipPath, info.ZipAssetName, progress, ct);

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败：SHA256 与服务器公布的不一致。");

            // ── 4. Extract ──────────────────────────────────────────────────────────
            progress.Report("正在解压更新包…");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("更新包解压失败：" + ex.Message);
            }

            // ── 5. Sanity check extracted contents ──────────────────────────────────
            var newAppExe     = Path.Combine(extractDir, AppExeName);
            var newUpdaterExe = Path.Combine(extractDir, UpdaterExeName);

            if (!File.Exists(newAppExe) || !File.Exists(newUpdaterExe))
                throw new InvalidDataException("更新包内容异常：缺少必要文件。");

            var actualFileVersion = FileVersionInfo.GetVersionInfo(newAppExe).FileVersion;
            if (string.IsNullOrEmpty(actualFileVersion) ||
                !Version.TryParse(actualFileVersion, out var parsedFv) ||
                NormalizeForCompare(parsedFv) != NormalizeForCompare(info.NewVersion))
            {
                throw new InvalidDataException(
                    $"更新包版本不匹配：期望 {info.NewVersion}，实际 {actualFileVersion}。");
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
                    "缺少升级器组件，请重新下载完整安装包。",
                    currentUpdater);
            }

            var stagedRunner = Path.Combine(runnerDir, UpdaterExeName);
            File.Copy(currentUpdater, stagedRunner, overwrite: true);

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

                var cutoff = DateTime.Now.AddHours(-24);
                foreach (var sub in Directory.EnumerateDirectories(AppPaths.UpdatesDir))
                {
                    try
                    {
                        if (Directory.GetCreationTime(sub) < cutoff)
                            Directory.Delete(sub, recursive: true);
                    }
                    catch { /* one bad dir shouldn't break the sweep */ }
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

        // System.Version normalizes missing components to -1; align so 1.2.3 == 1.2.3.0.
        private static (int, int, int, int) NormalizeForCompare(Version v) =>
            (v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

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
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"XrayUI/{AppVersion.Current}");
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
            IProgress<string> progress, CancellationToken ct)
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

        private static string FormatProgress(string name, long received, long? total)
        {
            var mbReceived = received / 1024.0 / 1024.0;
            return total.HasValue
                ? $"正在下载 {name} … {mbReceived:0.0} / {total.Value / 1024.0 / 1024.0:0.0} MB"
                : $"正在下载 {name} … {mbReceived:0.0} MB";
        }
    }
}
