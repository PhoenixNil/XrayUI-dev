using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Tests;

public class ChangelogSelectorTests
{
    private static ChangelogFeed Feed(params ChangelogVersion[] versions) =>
        new() { Versions = [.. versions] };

    private static ChangelogVersion V(string version, string[]? zh = null, string[]? en = null) =>
        new()
        {
            Version = version,
            Zh = zh is null ? null : [.. zh],
            En = en is null ? null : [.. en],
        };

    [Fact]
    public void PicksChineseWhenLanguageIsChinese()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            "zh");

        Assert.Equal(["中文条目"], result);
    }

    [Fact]
    public void PicksEnglishForNonChineseLanguage()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            "en");

        Assert.Equal(["English line"], result);
    }

    [Fact]
    public void FallsBackToOtherLanguageWhenPreferredMissing()
    {
        var zhOnly = ChangelogSelector.SelectLatest(
            Feed(V("1.18", zh: ["只有中文"])),
            "en");
        Assert.Equal(["只有中文"], zhOnly);

        var enOnly = ChangelogSelector.SelectLatest(
            Feed(V("1.18", en: ["English only"])),
            "zh");
        Assert.Equal(["English only"], enOnly);
    }

    [Fact]
    public void ReadsOnlyTheFirstVersion()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", en: ["latest"]), V("1.17", en: ["older"])),
            "en");

        Assert.Equal(["latest"], result);
    }

    [Fact]
    public void DoesNotFallBackToAnOlderVersionWhenLatestHasNoNotes()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", en: [""]), V("1.17", en: ["older"])),
            "en");

        Assert.Empty(result);
    }

    [Fact]
    public void DropsBlankLinesAndTrims()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", en: ["  padded  ", "", "   "])),
            "en");

        Assert.Equal(["padded"], result);
    }

    [Fact]
    public void ReturnsEmptyForMissingOrEmptyFeed()
    {
        Assert.Empty(ChangelogSelector.SelectLatest(null, "en"));
        Assert.Empty(ChangelogSelector.SelectLatest(new ChangelogFeed(), "en"));
        Assert.Empty(ChangelogSelector.SelectLatest(Feed(), "en"));
    }

    [Fact]
    public void TreatsNullLanguageAsEnglish()
    {
        var result = ChangelogSelector.SelectLatest(
            Feed(V("1.18", zh: ["中文"], en: ["English"])),
            null);

        Assert.Equal(["English"], result);
    }
}
