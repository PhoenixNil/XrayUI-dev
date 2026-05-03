using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayUI.Services
{
    internal static class FinalmaskJson
    {
        // Compact + default escaping — output is embedded in share URLs, where
        // the conservative default encoder (escapes `<`, `>`, `&`, `+`) is the
        // right choice. Storage path reuses AppJsonSerializerContext.WriteReadable
        // (indented + relaxed escaping, appropriate for the local config file).
        private static readonly JsonSerializerOptions CompactJson = new()
        {
            WriteIndented = false
        };

        public static string NormalizeForStorage(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var node = Parse(value);
            return node?.ToJsonString(AppJsonSerializerContext.WriteReadable) ?? value;
        }

        public static string NormalizeForShare(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var node = Parse(value);
            return node?.ToJsonString(CompactJson) ?? value;
        }

        public static JsonNode? Parse(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
