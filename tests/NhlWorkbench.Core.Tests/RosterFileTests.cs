using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Roster;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Reads the real roster data when it's on this machine. Everything edited here is a copy in memory.</summary>
public class RosterFileTests
{
    // The roster save carried by the 26-27 mod installed next to the app. Never written to.
    private static readonly string ModSave = Path.Combine(
        SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native", "mods", "26-27 Conversion.zip (1)", "roster", "SYS-DATA");

    private static RosterFile? Disc()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return null;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        return RosterFile.OpenDatabase(game.ReadDecoded(game.Find("db/nhlng.db")!.Winner).Value!).Value;
    }

    private static RosterFile? Save() => File.Exists(ModSave) ? RosterFile.OpenSave(File.ReadAllBytes(ModSave)).Value : null;

    private static RosterPlayer Named(RosterFile roster, int team, string lastName) => roster.PlayersOn(team).First(p => p.LastName == lastName);

    [Fact]
    public void KnownPlayersReadAsThemselves()
    {
        if (Disc() is not { } roster)
        {
            return;
        }

        Assert.False(roster.IsEditable);
        RosterPlayer crosby = Named(roster, 23, "Crosby");
        Assert.Equal("Sidney Crosby", crosby.FullName);
        Assert.Equal(87, crosby.Jersey);
        Assert.Equal("C", crosby.PositionName);
        Assert.Equal(RosterFile.Captain, crosby.Captain);
        Assert.Equal(96, crosby.Overall);
        Assert.Equal(71, crosby.HeightInches);
        Assert.Equal(200, crosby.WeightPounds);
        Assert.Equal((1987, 8, 7), (crosby.BirthYear, crosby.BirthMonth, crosby.BirthDay));
        Assert.Equal("Line 1 C", crosby.Line);
        Assert.Equal("Playmaker", crosby.StyleName);

        RosterPlayer price = Named(roster, 15, "Price");
        Assert.True(price.IsGoalie);
        Assert.Equal("Starter", price.Line);
        Assert.Equal(92, price.Overall);
        Assert.Equal(92, roster.RatingOf(price, "c_overall"));
    }

    [Fact]
    public void EveryTeamReadsAsAFullRoster()
    {
        if (Disc() is not { } roster || !SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        foreach (GameTeam team in catalog.Teams.Where(t => t.HasDatabaseRow && t.LeagueCode == 0))
        {
            IReadOnlyList<RosterPlayer> players = roster.PlayersOn(team.Index);
            Assert.InRange(players.Count, 20, 35);
            Assert.Equal(2, players.Count(p => p.Line is "Starter" or "Backup"));
            Assert.Equal(12, players.Count(p => p.Line.StartsWith("Line", StringComparison.Ordinal)));
            Assert.Equal(6, players.Count(p => p.Line.StartsWith("Pair", StringComparison.Ordinal)));
            Assert.True(players.Count(p => p.Captain == RosterFile.Captain) <= 1, team.Name);
        }
    }

    [Fact]
    public void TheBuiltInRosterRefusesEveryEdit()
    {
        if (Disc() is not { } roster)
        {
            return;
        }

        RosterPlayer player = roster.PlayersOn(23)[0];
        Assert.False(roster.SetRating(player, "c_speed", 80).IsSuccess);
        Assert.False(roster.SetName(player, "A", "B").IsSuccess);
        Assert.False(roster.BuildSave().IsSuccess);
        Assert.False(roster.Trade(player, roster.PlayersOn(0)[0]).IsSuccess);
    }

    [Fact]
    public void EditsToASaveSurviveReopeningIt()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        Assert.True(roster.IsEditable);
        RosterPlayer crosby = Named(roster, 23, "Crosby");
        Assert.True(roster.SetName(crosby, "Sid", "The Kid").IsSuccess);
        Assert.True(roster.SetRating(crosby, "c_speed", 77).IsSuccess);
        Assert.True(roster.SetHeight(crosby, 72).IsSuccess);
        Assert.True(roster.SetWeight(crosby, 210).IsSuccess);
        Assert.True(roster.SetBirthDate(crosby, 1999, 2, 28).IsSuccess);
        Assert.True(roster.SetHandedness(crosby, 1).IsSuccess);
        Assert.True(roster.SetPosition(crosby, 2).IsSuccess);
        Assert.True(roster.SetStyle(crosby, 7).IsSuccess);

        var built = roster.BuildSave();
        Assert.True(built.IsSuccess, built.Error);
        Assert.Equal(new FileInfo(ModSave).Length, built.Value!.Length);

        RosterFile reopened = RosterFile.OpenSave(built.Value).Value!;
        Assert.Empty(reopened.ChecksumProblems);
        RosterPlayer after = Named(reopened, 23, "The Kid");
        Assert.Equal("Sid The Kid", after.FullName);
        Assert.Equal(77, reopened.RatingOf(after, "c_speed"));
        Assert.Equal(72, after.HeightInches);
        Assert.Equal(210, after.WeightPounds);
        Assert.Equal((1999, 2, 28), (after.BirthYear, after.BirthMonth, after.BirthDay));
        Assert.Equal(1, after.Handedness);
        Assert.Equal("RW", after.PositionName);
        Assert.Equal("Sniper", after.StyleName);

        // The same player's other team sees the shared changes.
        Assert.Contains(reopened.PlayersOn(135), p => p.FullName == "Sid The Kid" && p.Style == 7);
    }

    [Fact]
    public void BadValuesAreRefused()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        RosterPlayer crosby = Named(roster, 23, "Crosby");
        Assert.False(roster.SetRating(crosby, "c_speed", 100).IsSuccess);
        Assert.False(roster.SetRating(crosby, "c_speed", 35).IsSuccess);
        Assert.False(roster.SetHeight(crosby, 20).IsSuccess);
        Assert.False(roster.SetWeight(crosby, 90).IsSuccess);
        Assert.False(roster.SetBirthDate(crosby, 1999, 2, 30).IsSuccess);
        Assert.False(roster.SetJersey(crosby, 100).IsSuccess);
        Assert.False(roster.SetName(crosby, "Sid", "  ").IsSuccess);
        Assert.False(roster.SetStyle(crosby, 0).IsSuccess);
        Assert.False(roster.SetName(crosby, "Sid", new string('x', 500)).IsSuccess);
        Assert.False(roster.SetPosition(crosby, RosterFile.GoaliePosition).IsSuccess);
    }

    [Fact]
    public void ATeamKeepsOneCaptainAndUniqueNumbers()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        IReadOnlyList<RosterPlayer> team = roster.PlayersOn(23);
        RosterPlayer other = team.First(p => p.LastName != "Crosby");
        Assert.True(roster.SetCaptain(other, RosterFile.Captain).IsSuccess);
        Assert.Single(roster.PlayersOn(23), p => p.Captain == RosterFile.Captain);
        Assert.Equal(other.PlayerId, roster.PlayersOn(23).Single(p => p.Captain == RosterFile.Captain).PlayerId);

        var refused = roster.SetJersey(other, 87);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Crosby", refused.Error);

        int free = Enumerable.Range(1, 98).First(n => team.All(p => p.Jersey != n));
        Assert.True(roster.SetJersey(other, free).IsSuccess);
        Assert.Equal(free, roster.PlayersOn(23).First(p => p.PlayerId == other.PlayerId).Jersey);
    }

    [Fact]
    public void ATradeSwapsPlayersAndLeavesTheRostersWhole()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        RosterPlayer crosby = Named(roster, 23, "Crosby");
        RosterPlayer ovechkin = Named(roster, 29, "Ovechkin");
        int pensSize = roster.PlayersOn(23).Count;
        int capsSize = roster.PlayersOn(29).Count;
        string pensLine = crosby.Line;
        string capsLine = ovechkin.Line;

        var traded = roster.Trade(crosby, ovechkin);
        Assert.True(traded.IsSuccess, traded.Error);

        // Check it on the saved file, not just in memory.
        RosterFile saved = RosterFile.OpenSave(roster.BuildSave().Value!).Value!;
        Assert.Equal(pensSize, saved.PlayersOn(23).Count);
        Assert.Equal(capsSize, saved.PlayersOn(29).Count);
        RosterPlayer ovechkinNow = Named(saved, 23, "Ovechkin");
        RosterPlayer crosbyNow = Named(saved, 29, "Crosby");
        Assert.DoesNotContain(saved.PlayersOn(23), p => p.LastName == "Crosby");
        Assert.DoesNotContain(saved.PlayersOn(29), p => p.LastName == "Ovechkin");

        // Lines stay with the spot, letters don't travel, and numbers stay unique.
        Assert.Equal(pensLine, ovechkinNow.Line);
        Assert.Equal(capsLine, crosbyNow.Line);
        Assert.Equal(RosterFile.NoCaptain, ovechkinNow.Captain);
        Assert.Equal(RosterFile.NoCaptain, crosbyNow.Captain);
        Assert.Equal(saved.PlayersOn(23).Count, saved.PlayersOn(23).Select(p => p.Jersey).Distinct().Count());
        Assert.Equal(saved.PlayersOn(29).Count, saved.PlayersOn(29).Select(p => p.Jersey).Distinct().Count());
        Assert.Equal(crosby.Jersey, crosbyNow.Jersey);
    }

    [Fact]
    public void ImpossibleTradesAreRefusedAndChangeNothing()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        byte[] before = roster.DatabaseBytes();
        IReadOnlyList<RosterPlayer> pens = roster.PlayersOn(23);
        IReadOnlyList<RosterPlayer> caps = roster.PlayersOn(29);
        Assert.False(roster.Trade(pens[0], pens[1]).IsSuccess);
        Assert.False(roster.Trade(pens.First(p => p.IsGoalie), caps.First(p => !p.IsGoalie)).IsSuccess);
        Assert.False(roster.Trade(pens[0], pens[0]).IsSuccess);
        Assert.True(before.AsSpan().SequenceEqual(roster.DatabaseBytes()));
    }

    [Fact]
    public void AFreeAgentCanBeSignedInSomeonesPlace()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        IReadOnlyList<RosterPlayer> agents = roster.PlayersOn(RosterFile.FreeAgents);
        Assert.NotEmpty(agents);
        Assert.All(agents, a => Assert.True(a.IsFreeAgent));
        RosterPlayer agent = agents.First(a => !a.IsGoalie);
        RosterPlayer leaving = roster.PlayersOn(23).First(p => !p.IsGoalie && p.LastName != "Crosby" && roster.PlayersOn(23).Count(q => q.PlayerId == p.PlayerId) == 1);
        int count = agents.Count;

        var traded = roster.Trade(leaving, agent);
        Assert.True(traded.IsSuccess, traded.Error);
        RosterFile saved = RosterFile.OpenSave(roster.BuildSave().Value!).Value!;
        Assert.Contains(saved.PlayersOn(23), p => p.PlayerId == agent.PlayerId);
        Assert.DoesNotContain(saved.PlayersOn(23), p => p.PlayerId == leaving.PlayerId);
        Assert.Contains(saved.PlayersOn(RosterFile.FreeAgents), p => p.PlayerId == leaving.PlayerId);
        Assert.DoesNotContain(saved.PlayersOn(RosterFile.FreeAgents), p => p.PlayerId == agent.PlayerId);
        Assert.Equal(count, saved.PlayersOn(RosterFile.FreeAgents).Count);
    }

    [Fact]
    public void GarbageIsRefusedWithoutThrowing()
    {
        Assert.False(RosterFile.OpenSave([1, 2, 3]).IsSuccess);
        Assert.False(RosterFile.OpenDatabase([1, 2, 3]).IsSuccess);
        Assert.False(RosterFile.OpenSave(new byte[4096]).IsSuccess);
    }

    [Fact]
    public void TheActiveRosterIsReadFromTheSettings()
    {
        string folder = Directory.CreateTempSubdirectory("roster-sources").FullName;
        try
        {
            Assert.Null(RosterSources.ActiveRosterName(folder));
            File.WriteAllText(Path.Combine(folder, "nhl_settings.ini"), "[video]\nroster = wrong\n[launcher]\nother = 1\nroster = My Mod\n[z]\nroster = also wrong\n");
            Assert.Equal("My Mod", RosterSources.ActiveRosterName(folder));

            Directory.CreateDirectory(Path.Combine(folder, "mods", "Another", "roster"));
            Directory.CreateDirectory(Path.Combine(folder, "mods", "My Mod", "roster"));
            Directory.CreateDirectory(Path.Combine(folder, "mods", "No Roster"));
            File.WriteAllBytes(Path.Combine(folder, "mods", "Another", "roster", "SYS-DATA"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "mods", "My Mod", "roster", "SYS-DATA"), [1]);
            IReadOnlyList<RosterSource> sources = RosterSources.Find(folder, null);
            Assert.Equal(["My Mod", "Another"], sources.Select(s => s.Title));
            Assert.Contains("chosen", sources[0].Note);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
