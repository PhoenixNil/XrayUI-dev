using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    /// <summary>
    /// Result of an AI API reachability check.
    /// </summary>
    public enum AiUnlockStatus
    {
        /// <summary>Not yet checked.</summary>
        Unknown,
        /// <summary>API endpoint is reachable (unlocked).</summary>
        Unlocked,
        /// <summary>API endpoint is blocked or unreachable.</summary>
        Blocked
    }

    /// <summary>
    /// Checks whether AI service API endpoints (OpenAI, Anthropic/Claude)
    /// are reachable through the local HTTP proxy.
    /// </summary>
    public sealed class AiUnlockCheckService
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        /// <summary>Countries where Gemini is not available (based on official supported list).</summary>
        private static readonly HashSet<string> GeminiBlockedCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "CN", // China
            "RU", // Russia
            "IR", // Iran
        };

        /// <summary>
        /// Check OpenAI API reachability.
        /// Sends a GET to https://api.openai.com/ and inspects the response body.
        /// If the body contains "unsupported_country_region_territory", the region is blocked.
        /// A 403 status also indicates blocking.
        /// </summary>
        public async Task<AiUnlockStatus> CheckOpenAiAsync(int httpProxyPort, CancellationToken ct = default)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
                    UseProxy = true
                };
                using var client = new HttpClient(handler) { Timeout = Timeout };

                var response = await client.GetAsync("https://api.openai.com/", ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                // Region / country block — OpenAI returns 200 but body contains this error code
                if (body.Contains("unsupported_country_region_territory", StringComparison.OrdinalIgnoreCase))
                    return AiUnlockStatus.Blocked;

                // Explicit 403 = blocked
                if ((int)response.StatusCode == 403)
                    return AiUnlockStatus.Blocked;

                return AiUnlockStatus.Unlocked;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // let caller handle external cancellation
            }
            catch
            {
                return AiUnlockStatus.Blocked;
            }
        }

        /// <summary>
        /// Check Anthropic (Claude) API reachability.
        /// Mirrors the bash logic: sends a HEAD-like request to https://api.anthropic.com/v1/messages.
        /// 401/400/405 → reachable (unlocked), 403 → blocked, timeout → blocked.
        /// </summary>
        public async Task<AiUnlockStatus> CheckClaudeAsync(int httpProxyPort, CancellationToken ct = default)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
                    UseProxy = true
                };
                using var client = new HttpClient(handler) { Timeout = Timeout };

                // Use GET like the bash curl -sI approach (HEAD may be blocked)
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/messages");
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                var code = (int)response.StatusCode;

                // 401, 400, 405 → API is reachable (just not authenticated)
                if (code == 401 || code == 400 || code == 405)
                    return AiUnlockStatus.Unlocked;

                // 403 → IP ban / blocked
                if (code == 403)
                    return AiUnlockStatus.Blocked;

                // Other codes: 2xx, 3xx → also reachable
                if (code >= 200 && code < 400)
                    return AiUnlockStatus.Unlocked;

                // 5xx or unknown → treat as blocked
                return AiUnlockStatus.Blocked;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Timeout or connection refused → blocked
                return AiUnlockStatus.Blocked;
            }
        }

        /// <summary>
        /// Check Gemini availability by resolving the proxy exit IP's country.
        /// Uses Cloudflare trace (primary) or ipinfo.io (fallback) to determine geolocation,
        /// then checks against the Gemini unsupported countries list.
        /// </summary>
        public async Task<AiUnlockStatus> CheckGeminiAsync(int httpProxyPort, CancellationToken ct = default)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
                    UseProxy = true
                };
                using var client = new HttpClient(handler) { Timeout = Timeout };

                // Primary: Cloudflare trace
                var country = await GetCountryFromCloudflareAsync(client, ct);

                // Fallback: ipinfo.io
                if (string.IsNullOrEmpty(country))
                    country = await GetCountryFromIpInfoAsync(client, ct);

                if (string.IsNullOrEmpty(country))
                    return AiUnlockStatus.Blocked;

                return GeminiBlockedCountries.Contains(country)
                    ? AiUnlockStatus.Blocked
                    : AiUnlockStatus.Unlocked;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return AiUnlockStatus.Blocked;
            }
        }

        /// <summary>
        /// Parse loc= from Cloudflare cdn-cgi/trace response.
        /// </summary>
        private static async Task<string?> GetCountryFromCloudflareAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                var body = await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", ct);
                foreach (var line in body.Split('\n'))
                {
                    if (line.StartsWith("loc=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(4).Trim();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // fall through to return null
            }
            return null;
        }

        /// <summary>
        /// Parse country from ipinfo.io/json response.
        /// </summary>
        private static async Task<string?> GetCountryFromIpInfoAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                var body = await client.GetStringAsync("https://ipinfo.io/json", ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("country", out var prop))
                    return prop.GetString();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // fall through to return null
            }
            return null;
        }
    }
}
