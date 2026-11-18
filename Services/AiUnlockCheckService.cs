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
                using var client = CreateProxiedClient(httpProxyPort);

                using var response = await client.GetAsync("https://api.openai.com/", ct);
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
                using var client = CreateProxiedClient(httpProxyPort);

                // Use GET like the bash curl -sI approach (HEAD may be blocked)
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/messages");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

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
        /// Check Gemini Web availability by using Gemini's own geo preflight RPC.
        /// </summary>
        public async Task<AiUnlockStatus> CheckGeminiAsync(int httpProxyPort, CancellationToken ct = default)
        {
            try
            {
                using var client = CreateProxiedClient(httpProxyPort);

                // RPC K4WWud is Gemini's geo preflight: the response carries the visitor's
                // resolved location plus Gemini's own "unsupported region" flag.
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://gemini.google.com/_/BardChatUi/data/batchexecute?rpcids=K4WWud");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("f.req", "[[[\"K4WWud\",\"[[1],[\\\"en-US\\\"]]\",null,\"generic\"]]]")
                });

                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return AiUnlockStatus.Blocked;

                var body = await response.Content.ReadAsStringAsync(ct);

                // Skip Google's anti-XSS prefix )]}'\n to reach the JSON array.
                var jsonStart = body.IndexOf("[[", StringComparison.Ordinal);
                if (jsonStart == -1)
                    return AiUnlockStatus.Blocked;

                using var doc = JsonDocument.Parse(body.AsMemory(jsonStart));

                // Outer: [["wrb.fr", "K4WWud", innerJsonString, ...]]
                if (!TryGetArrayItem(doc.RootElement, 0, out var outer) ||
                    !TryGetArrayItem(outer, 2, out var innerJsonElem))
                    return AiUnlockStatus.Blocked;

                var innerJsonStr = innerJsonElem.GetString();
                if (string.IsNullOrEmpty(innerJsonStr))
                    return AiUnlockStatus.Blocked;

                using var innerDoc = JsonDocument.Parse(innerJsonStr);

                // Inner: [[locationDisplayName, ...]] — the visitor's resolved location.
                if (!TryGetArrayItem(innerDoc.RootElement, 0, out var inner) ||
                    !TryGetArrayItem(inner, 0, out var locationElem))
                    return AiUnlockStatus.Blocked;

                var location = locationElem.ValueKind == JsonValueKind.String ? locationElem.GetString() : null;
                if (string.IsNullOrEmpty(location))
                    return AiUnlockStatus.Blocked;

                // Availability is decided purely from the API-reported location: mainland China,
                // Russia, Iran, North Korea, Syria and Cuba are treated as blocked.
                return IsBlockedRegion(location) ? AiUnlockStatus.Blocked : AiUnlockStatus.Unlocked;
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

        /// <summary>Build an <see cref="HttpClient"/> routed through the local HTTP proxy.</summary>
        private static HttpClient CreateProxiedClient(int httpProxyPort)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpProxyPort}"),
                UseProxy = true
            };
            // HttpClient disposes the handler together with itself.
            return new HttpClient(handler) { Timeout = Timeout };
        }

        /// <summary>Index into a JSON array only when it is an array long enough to hold <paramref name="index"/>.</summary>
        private static bool TryGetArrayItem(JsonElement parent, int index, out JsonElement item)
        {
            if (parent.ValueKind == JsonValueKind.Array && parent.GetArrayLength() > index)
            {
                item = parent[index];
                return true;
            }
            item = default;
            return false;
        }

        // Mainland China is blocked, but its SARs and Taiwan are not.
        private static readonly string[] ChinaTerms = { "China", "中国" };
        private static readonly string[] ChinaExceptions = { "Hong Kong", "香港", "Macau", "Macao", "澳门", "Taiwan", "台湾" };

        // Regions where Gemini is unavailable regardless of its own flag.
        private static readonly string[] OtherBlockedTerms =
        {
            "Russia", "俄罗斯",
            "Iran", "伊朗",
            "North Korea", "朝鲜",
            "Syria", "叙利亚",
            "Cuba", "古巴"
        };

        private static bool IsBlockedRegion(string location)
        {
            bool isChinaMainland = ContainsAny(location, ChinaTerms) && !ContainsAny(location, ChinaExceptions);
            return isChinaMainland || ContainsAny(location, OtherBlockedTerms);
        }

        private static bool ContainsAny(string text, string[] terms)
        {
            foreach (var term in terms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
