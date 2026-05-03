using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    // Minimal subset of the GitHub Releases API response — only the fields we use.
    internal sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]   public string? TagName { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")]      public bool Draft { get; set; }
        [JsonPropertyName("assets")]     public List<GhAsset>? Assets { get; set; }
    }

    internal sealed class GhAsset
    {
        [JsonPropertyName("name")]                 public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
    }

    public sealed record UpdateInfo(
        Version NewVersion,
        string TagName,
        string ZipUrl,
        string Sha256Url,
        string ZipAssetName);
}
