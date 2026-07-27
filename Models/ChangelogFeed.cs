using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    /// <summary>
    /// Shape of <c>https://www.xrayui.site/changelog.json</c> — user-facing release notes,
    /// maintained on the website rather than in the GitHub release body so the release
    /// page can stay a plain technical PR list. One entry per version — order is not
    /// meaningful, the client looks its target version up by name — and both languages
    /// live side by side so one request is enough and a missing translation is visible
    /// at a glance while editing.
    /// </summary>
    internal sealed class ChangelogFeed
    {
        [JsonPropertyName("versions")] public List<ChangelogVersion>? Versions { get; set; }
    }

    internal sealed class ChangelogVersion
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("zh")]      public List<string>? Zh { get; set; }
        [JsonPropertyName("en")]      public List<string>? En { get; set; }
    }
}
