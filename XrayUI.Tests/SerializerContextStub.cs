using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrayUI.Services
{
    // Test-side stand-in for the app's source-generated AppJsonSerializerContext.
    // Linked production sources (FinalmaskJson) only touch WriteReadable, and only
    // for JsonNode.ToJsonString — which reads writer settings, never the type
    // resolver. Options must stay byte-for-byte equivalent to the real ones
    // (WriteIndented + relaxed escaping) or round-trip string assertions drift.
    internal static class AppJsonSerializerContext
    {
        public static JsonSerializerOptions WriteReadable { get; } = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
