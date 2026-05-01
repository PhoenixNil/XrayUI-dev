using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using XrayUI.Models;

namespace XrayUI.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ServerEntry>))]
[JsonSerializable(typeof(ServerEntry))]
[JsonSerializable(typeof(List<CustomRoutingRule>))]
[JsonSerializable(typeof(CustomRoutingRule))]
[JsonSerializable(typeof(List<SubscriptionEntry>))]
[JsonSerializable(typeof(SubscriptionEntry))]
[JsonSerializable(typeof(PresetSettings))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
    // Write-side options that emit CJK / emoji as literal UTF-8 instead of \uXXXX escapes.
    // Safe here because output is a local config file, never embedded in HTML / JS / URL.
    // Lazy so `Default` is guaranteed initialized by the time the factory runs — a static
    // field initializer that touches `Default` directly can race with source-gen's own init.
    private static readonly Lazy<JsonSerializerOptions> _writeReadable = new(() =>
        new JsonSerializerOptions(Default!.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    public static JsonSerializerOptions WriteReadable => _writeReadable.Value;

    public static JsonTypeInfo<T> Readable<T>() =>
        (JsonTypeInfo<T>)WriteReadable.GetTypeInfo(typeof(T));
}
