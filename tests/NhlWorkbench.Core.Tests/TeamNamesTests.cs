using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class TeamNamesTests
{
    private static TdbLanguageFile? English()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return null;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        return TdbLanguageFile.Parse(game.ReadDecoded(game.Find("fe/loc/nhl_eng_us.db")!.Winner).Value!).Value;
    }

    [Fact]
    public void ATeamsNameIsFoundEverywhereItIsWritten()
    {
        if (English() is not { } file)
        {
            return;
        }

        IReadOnlyList<TeamNamePart> parts = TeamNames.Read(file, "PIT", [23]);
        Assert.Equal([TeamNames.FullName, TeamNames.City, TeamNames.Nickname, TeamNames.ShortNickname, TeamNames.Abbreviation], parts.Select(p => p.Label));
        Assert.Contains("NHLTeamName_23", parts[0].Keys);
        Assert.Contains("NHLTeamName_PIT", parts[0].Keys);
        Assert.Contains("CITYLINE1_23", parts[1].Keys);
        Assert.Contains("TEAMLINE2_PIT", parts[2].Keys);
        Assert.Equal("Pens", parts[3].Text);
        Assert.Equal("PIT", parts[4].Text);
    }

    [Fact]
    public void ANumberHoldingAnotherTeamsNameIsLeftOut()
    {
        if (English() is not { } file)
        {
            return;
        }

        IReadOnlyList<TeamNamePart> parts = TeamNames.Read(file, "PIT", [23, 15]);
        Assert.DoesNotContain("NHLTeamName_15", parts[0].Keys);
        Assert.Empty(TeamNames.Read(file, "NOPE", [23]));
    }

    [Fact]
    public void RenamingTouchesOnlyThatTeamAndCanBePutBack()
    {
        if (English() is not { } file || English() is not { } original)
        {
            return;
        }

        IReadOnlyList<TeamNamePart> parts = TeamNames.Read(file, "PIT", [23]);
        var before = file.Entries().ToDictionary(e => e.Key, e => e.Text);
        (int changed, IReadOnlyList<string> problems) = TeamNames.Apply(file, parts, new Dictionary<string, string> { [TeamNames.City] = "Steeltown", [TeamNames.Abbreviation] = "STL" });
        Assert.Empty(problems);
        Assert.Equal(parts[1].Keys.Count + parts[4].Keys.Count, changed);

        List<string> different = file.Entries().Where(e => before[e.Key] != e.Text).Select(e => e.Key).ToList();
        Assert.Equal(parts[1].Keys.Concat(parts[4].Keys).Order(), different.Order());
        Assert.True(file.TryGetText("CITYLINE1_23", out string city) && city == "Steeltown");
        Assert.False(TeamNames.SameText(file, original));

        Assert.Equal(changed, TeamNames.PutBack(file, original, parts));
        Assert.True(TeamNames.SameText(file, original));
    }

    [Fact]
    public void TextCanBeReplacedThroughoutAFile()
    {
        if (English() is not { } file)
        {
            return;
        }

        int expected = file.Entries().Count(e => e.Text.Contains("Penguins", StringComparison.Ordinal));
        Assert.True(expected > 0);
        Assert.Equal(expected, GameTextSheet.CountMatches(file, "Penguins", matchCase: true));
        Assert.True(GameTextSheet.CountMatches(file, "penguins", matchCase: false) >= expected);
        Assert.Equal(0, GameTextSheet.CountMatches(file, "penguins", matchCase: true));
        Assert.Equal(0, GameTextSheet.CountMatches(file, string.Empty, matchCase: true));

        GameTextImport done = GameTextSheet.ReplaceAll(file, "Penguins", "Pens", matchCase: true);
        Assert.Empty(done.Problems);
        Assert.Equal(expected, done.Changed);
        Assert.True(file.TryGetText("TXT_NICKNAME_PIT", out string nickname) && nickname == "Pens");
        Assert.Equal(0, GameTextSheet.CountMatches(file, "Penguins", matchCase: true));
    }
}
