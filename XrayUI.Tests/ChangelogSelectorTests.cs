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
        var result = ChangelogSelector.Select(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            new Version(1, 17), new Version(1, 18), "zh");

        Assert.Single(result);
        Assert.Equal(["中文条目"], result[0].Lines);
    }

    [Fact]
    public void PicksEnglishForNonChineseLanguage()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", zh: ["中文条目"], en: ["English line"])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Equal(["English line"], result[0].Lines);
    }

    [Fact]
    public void FallsBackToOtherLanguageWhenPreferredMissing()
    {
        var zhOnly = ChangelogSelector.Select(
            Feed(V("1.18", zh: ["只有中文"])),
            new Version(1, 17), new Version(1, 18), "en");
        Assert.Equal(["只有中文"], zhOnly[0].Lines);

        var enOnly = ChangelogSelector.Select(
            Feed(V("1.18", en: ["English only"])),
            new Version(1, 17), new Version(1, 18), "zh");
        Assert.Equal(["English only"], enOnly[0].Lines);
    }

    [Fact]
    public void ExcludesVersionsAlreadyInstalled()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.17", en: ["old"]), V("1.18", en: ["new"])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Single(result);
        Assert.Equal(new Version(1, 18), result[0].Version);
    }

    [Fact]
    public void ExcludesVersionsBeyondTheTarget()
    {
        // The feed can already list a release newer than the one being offered
        // (e.g. notes pushed ahead of the GitHub release).
        var result = ChangelogSelector.Select(
            Feed(V("1.18", en: ["target"]), V("1.19", en: ["future"])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Single(result);
        Assert.Equal(new Version(1, 18), result[0].Version);
    }

    [Fact]
    public void SpansEveryVersionInRangeNewestFirst()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", en: ["a"]), V("1.20", en: ["c"]), V("1.19", en: ["b"])),
            new Version(1, 17), new Version(1, 20), "en");

        Assert.Equal(
            [new Version(1, 20), new Version(1, 19), new Version(1, 18)],
            result.Select(e => e.Version));
    }

    [Fact]
    public void CapsAtMaxVersionsKeepingTheNewest()
    {
        // Feed deliberately out of order so this also proves the cap runs after sorting.
        var result = ChangelogSelector.Select(
            Feed(
                V("1.11", en: ["oldest"]),
                V("1.15", en: ["e"]),
                V("1.12", en: ["b"]),
                V("1.14", en: ["d"]),
                V("1.16", en: ["newest"]),
                V("1.13", en: ["c"])),
            new Version(1, 10), new Version(1, 16), "en");

        Assert.Equal(ChangelogSelector.MaxVersions, result.Count);
        Assert.Equal(
            [new Version(1, 16), new Version(1, 15), new Version(1, 14), new Version(1, 13)],
            result.Select(e => e.Version));
    }

    [Fact]
    public void DoesNotPadWhenFewerVersionsThanTheCap()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", en: ["only one"])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Single(result);
    }

    [Fact]
    public void TreatsMissingVersionComponentsAsZero()
    {
        // "1.18" from the feed must not read as newer than an installed 1.18.0.
        var result = ChangelogSelector.Select(
            Feed(V("1.18", en: ["same version"])),
            new Version(1, 18, 0), new Version(1, 19), "en");

        Assert.Empty(result);
    }

    [Fact]
    public void DropsBlankLinesAndTrims()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", en: ["  padded  ", "", "   "])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Equal(["padded"], result[0].Lines);
    }

    [Fact]
    public void SkipsVersionsWithNoUsableLines()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", zh: [""], en: []), V("1.19", en: ["real"])),
            new Version(1, 17), new Version(1, 19), "zh");

        Assert.Single(result);
        Assert.Equal(new Version(1, 19), result[0].Version);
    }

    [Fact]
    public void SkipsUnparseableVersionStrings()
    {
        var result = ChangelogSelector.Select(
            Feed(V("not-a-version", en: ["junk"]), V("1.18", en: ["good"])),
            new Version(1, 17), new Version(1, 18), "en");

        Assert.Single(result);
        Assert.Equal(["good"], result[0].Lines);
    }

    [Fact]
    public void ReturnsEmptyForMissingOrEmptyFeed()
    {
        var current = new Version(1, 17);
        var target = new Version(1, 18);

        Assert.Empty(ChangelogSelector.Select(null, current, target, "en"));
        Assert.Empty(ChangelogSelector.Select(new ChangelogFeed(), current, target, "en"));
        Assert.Empty(ChangelogSelector.Select(Feed(), current, target, "en"));
    }

    [Fact]
    public void TreatsNullLanguageAsEnglish()
    {
        var result = ChangelogSelector.Select(
            Feed(V("1.18", zh: ["中文"], en: ["English"])),
            new Version(1, 17), new Version(1, 18), null);

        Assert.Equal(["English"], result[0].Lines);
    }
}
