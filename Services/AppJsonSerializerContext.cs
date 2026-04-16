using System.Collections.Generic;
using System.Text.Json.Serialization;
using XrayUI.Models;

namespace XrayUI.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ServerEntry>))]
[JsonSerializable(typeof(ServerEntry))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
