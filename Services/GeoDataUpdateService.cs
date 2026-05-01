using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    /// <summary>
    /// Downloads geoip.dat / geosite.dat from Loyalsoldier/v2ray-rules-dat.
    /// Optimized over v2rayN: fetches the tiny .sha256sum first and skips the big download
    /// when the local file already matches. The hash also verifies downloaded data integrity.
    /// </summary>
    public class GeoDataUpdateService
    {
        private const string UrlTemplate =
            "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/{0}.dat";

        private static readonly string[] Files = { "geosite", "geoip" };

        /// <summary>Result of an update run. AnyUpdated == true iff at least one .dat was actually replaced.</summary>
        public readonly record struct UpdateResult(int UpdatedCount, int SkippedCount)
        {
            public bool AnyUpdated => UpdatedCount > 0;
        }

        /// <param name="proxyUrl">
        /// Optional proxy, e.g. "socks5://127.0.0.1:16890". When set, all HTTP traffic
        /// (both the tiny .sha256sum fetch and the .dat download) is tunnelled through it.
        /// Caller typically passes the running xray's local SOCKS port; null for direct.
        /// </param>
        public async Task<UpdateResult> UpdateAsync(IProgress<string> progress, string? proxyUrl, CancellationToken ct)
        {
            using var handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                handler.Proxy    = new WebProxy(proxyUrl);
                handler.UseProxy = true;
                progress.Report($"通过本地代理下载（{proxyUrl}）…");
            }
            else
            {
                // Explicitly disable — .NET's default picks up WinHTTP proxy config which is
                // rarely what the user expects from an xray UI. Be direct and predictable.
                handler.UseProxy = false;
            }

            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XrayUI");

            Directory.CreateDirectory(XrayService.RulesDir);

            int updated = 0;
            int skipped = 0;

            foreach (var name in Files)
            {
                ct.ThrowIfCancellationRequested();

                var url    = string.Format(UrlTemplate, name);
                var sumUrl = url + ".sha256sum";
                var target = Path.Combine(XrayService.RulesDir, $"{name}.dat");

                progress.Report($"正在检查 {name}.dat …");

                // If the hash fetch fails (404, network), fall through to unconditional download — v2rayN parity.
                string? remoteHash = await TryFetchRemoteHashAsync(client, sumUrl, ct);

                if (remoteHash != null && File.Exists(target))
                {
                    var localHash = await ComputeSha256Async(target, ct);
                    if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                    {
                        progress.Report($"{name}.dat 已是最新");
                        skipped++;
                        continue;
                    }
                }

                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dat");
                try
                {
                    await DownloadToFileAsync(client, url, tmp, $"{name}.dat", progress, ct);

                    if (remoteHash != null)
                    {
                        var downloadedHash = await ComputeSha256Async(tmp, ct);
                        if (!string.Equals(downloadedHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"{name}.dat 校验失败：下载文件的 SHA256 与服务器公布的不一致。");
                        }
                    }

                    File.Move(tmp, target, overwrite: true);
                    updated++;
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }

            return new UpdateResult(updated, skipped);
        }

        private static async Task<string?> TryFetchRemoteHashAsync(HttpClient client, string sumUrl, CancellationToken ct)
        {
            try
            {
                var text = await client.GetStringAsync(sumUrl, ct);
                return ParseSha256SumLine(text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Sum file missing or unreachable — not fatal, caller falls back to blind download.
                return null;
            }
        }

        /// <summary>
        /// Parses a sha256sum line: "&lt;64-hex&gt; [*]filename". Accepts raw-hash-only too.
        /// Returns null if the content doesn't look like a valid SHA256.
        /// </summary>
        private static string? ParseSha256SumLine(string content)
        {
            var line = content.Trim();
            if (line.Length == 0) return null;

            // Take the first whitespace-delimited token.
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

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task DownloadToFileAsync(
            HttpClient client,
            string url,
            string destPath,
            string displayName,
            IProgress<string> progress,
            CancellationToken ct)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            progress.Report(FormatProgress(displayName, 0, total));

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer      = new byte[81920];
            long received   = 0;
            long lastReport = 0;

            while (true)
            {
                var read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;

                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                if (received - lastReport >= 512 * 1024)
                {
                    progress.Report(FormatProgress(displayName, received, total));
                    lastReport = received;
                }
            }

            progress.Report(FormatProgress(displayName, received, total));
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
