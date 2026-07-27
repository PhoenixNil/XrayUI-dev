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

    private static List<string> Select(ChangelogFeed? feed, string target, string? language) =>
        ChangelogSelector.SelectForVersion(feed, Version.Parse(target), language);

    [Fact]
    public void PicksChineseWhenLanguageIsChinese()
    {
        var result = Select(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            "1.18", "zh");

        Assert.Equal(["中文条目"], result);
    }

    [Fact]
    public void PicksEnglishForNonChineseLanguage()
    {
        var result = Select(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            "1.18", "en");

        Assert.Equal(["English line"], result);
    }

    [Fact]
    public void FallsBackToOtherLanguageWhenPreferredMissing()
    {
        var zhOnly = Select(Feed(V("1.18", zh: ["只有中文"])), "1.18", "en");
        Assert.Equal(["只有中文"], zhOnly);

        var enOnly = Select(Feed(V("1.18", en: ["English only"])), "1.18", "zh");
        Assert.Equal(["English only"], enOnly);
    }

    [Fact]
    public void PicksTheTargetEntryRegardlessOfPosition()
    {
        var result = Select(
            Feed(V("1.19", en: ["not out yet"]), V("1.18", en: ["being installed"])),
            "1.18", "en");

        Assert.Equal(["being installed"], result);
    }

    /// <summary>
    /// The release gate puts a version's notes on the site before its tag is pushed, so
    /// between those two moments the newest entry is not the one clients are offered.
    /// Showing it anyway would label the upcoming release's notes as the current one.
    /// </summary>
    [Fact]
    public void ReturnsEmptyWhenTheTargetVersionIsAbsent()
    {
        var result = Select(
            Feed(V("1.19", en: ["not out yet"]), V("1.17", en: ["already installed"])),
            "1.18", "en");

        Assert.Empty(result);
    }

    [Fact]
    public void MatchesRegardlessOfVersionPrecision()
    {
        var result = Select(Feed(V("1.19", en: ["notes"])), "1.19.0", "en");

        Assert.Equal(["notes"], result);
    }

    [Fact]
    public void DoesNotFallBackToAnotherVersionWhenTheTargetHasNoNotes()
    {
        var result = Select(
            Feed(V("1.18", en: [""]), V("1.17", en: ["older"])),
            "1.18", "en");

        Assert.Empty(result);
    }

    [Fact]
    public void DropsBlankLinesAndTrims()
    {
        var result = Select(
            Feed(V("1.18", en: ["  padded  ", "", "   "])),
            "1.18", "en");

        Assert.Equal(["padded"], result);
    }

    [Fact]
    public void ReturnsEmptyForMissingOrEmptyFeed()
    {
        Assert.Empty(Select(null, "1.18", "en"));
        Assert.Empty(Select(new ChangelogFeed(), "1.18", "en"));
        Assert.Empty(Select(Feed(), "1.18", "en"));
    }

    [Fact]
    public void IgnoresEntriesWithAnUnparseableVersion()
    {
        var result = Select(
            Feed(V("nightly", en: ["junk"]), V("1.18", en: ["real"])),
            "1.18", "en");

        Assert.Equal(["real"], result);
    }

    [Fact]
    public void TreatsNullLanguageAsEnglish()
    {
        var result = Select(
            Feed(V("1.18", zh: ["中文"], en: ["English"])),
            "1.18", null);

        Assert.Equal(["English"], result);
    }
}
