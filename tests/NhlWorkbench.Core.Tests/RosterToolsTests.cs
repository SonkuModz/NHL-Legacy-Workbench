using NhlWorkbench.Core.Roster;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Works on copies in memory of the roster save carried by the 26-27 mod. That file is never written to.</summary>
public class RosterToolsTests
{
    private const int Pittsburgh = 23;

    private static readonly string ModSave = Path.Combine(
        SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native", "mods", "26-27 Conversion.zip (1)", "roster", "SYS-DATA");

    private static RosterFile? Save() => File.Exists(ModSave) ? RosterFile.OpenSave(File.ReadAllBytes(ModSave)).Value : null;

    private static int BusyTeam(RosterFile roster) =>
        roster.TeamsOnRoster().Contains(Pittsburgh) && roster.PlayersOn(Pittsburgh).Count >= 18 ? Pittsburgh : roster.TeamsOnRoster().First(t => roster.PlayersOn(t).Count >= 18);

    [Fact]
    public void AFullTeamHasEverySpotHeldOnce()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        IReadOnlyDictionary<string, RosterPlayer> lineup = roster.LineupOf(team);
        foreach (RosterLineUnit unit in roster.LineUnitsHere().Where(u => u.Section == RosterFile.EvenStrength))
        {
            foreach (RosterLineSpot spot in unit.Spots)
            {
                Assert.True(lineup.ContainsKey(spot.Field), $"{unit.Title} {spot.Label}");
            }
        }

        Assert.Empty(roster.LineProblems(team));
        Assert.True(lineup["g1"].IsGoalie);
        Assert.False(lineup["l1c"].IsGoalie);
    }

    [Fact]
    public void MovingAPlayerSwapsThemWithWhoeverTheyReplace()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        IReadOnlyDictionary<string, RosterPlayer> before = roster.LineupOf(team);
        RosterPlayer one = before["l1c"];
        RosterPlayer two = before["l2c"];
        Assert.True(roster.SetLineSpot(team, "l1c", two).IsSuccess);

        IReadOnlyDictionary<string, RosterPlayer> after = roster.LineupOf(team);
        Assert.Equal(two.PlayerId, after["l1c"].PlayerId);
        Assert.Equal(one.PlayerId, after["l2c"].PlayerId);
        Assert.Empty(roster.LineProblems(team));
        Assert.StartsWith("Line 1", roster.Reread(two)!.Line);
    }

    [Fact]
    public void ASpotCanBeEmptiedAndThatIsReported()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        Assert.True(roster.SetLineSpot(team, "l4rw", null).IsSuccess);
        Assert.False(roster.LineupOf(team).ContainsKey("l4rw"));
        Assert.Contains(roster.LineProblems(team), p => p.Contains("Line 4") && p.Contains("RW"));
    }

    [Fact]
    public void GoaliesAndSkatersKeepToTheirOwnSpots()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        IReadOnlyDictionary<string, RosterPlayer> lineup = roster.LineupOf(team);
        Assert.False(roster.SetLineSpot(team, "l1c", lineup["g1"]).IsSuccess);
        Assert.False(roster.SetLineSpot(team, "g1", lineup["l1c"]).IsSuccess);
        Assert.False(roster.SetLineSpot(team, "nonsense", lineup["l1c"]).IsSuccess);
    }

    [Fact]
    public void BestLinesFillsEverySpotAndRanksTheTopLine()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        Assert.True(roster.SetBestLines(team).IsSuccess);
        Assert.Empty(roster.LineProblems(team));

        IReadOnlyDictionary<string, RosterPlayer> lineup = roster.LineupOf(team);
        Assert.True(lineup["l1c"].Overall >= lineup["l2c"].Overall);
        Assert.True(lineup["g1"].Overall >= lineup["g2"].Overall);
        Assert.Equal(lineup["l1c"].PlayerId, lineup["pp1c"].PlayerId);
        Assert.Equal(5, new[] { "s1", "s2", "s3", "s4", "s5" }.Select(f => lineup[f].PlayerId).Distinct().Count());

        // Nobody plays twice at even strength.
        var even = roster.LineUnitsHere().Where(u => u.Group == "even").SelectMany(u => u.Spots).Select(s => lineup[s.Field].PlayerId).ToList();
        Assert.Equal(even.Count, even.Distinct().Count());
    }

    [Fact]
    public void BulkRatingsStopAtTheLimitsAndCountPlayersOnce()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        RosterPlayer player = roster.PlayersOn(team).First(p => !p.IsGoalie);
        int speed = roster.RatingOf(player, "c_speed");
        int overall = roster.RatingOf(player, "c_overall");

        var twice = new[] { player, player };
        Assert.True(roster.AdjustRatings(twice, -2, "Skating").IsSuccess);
        Assert.Equal(Math.Max(speed - 2, RosterFile.LowestRating), roster.RatingOf(player, "c_speed"));
        Assert.Equal(overall, roster.RatingOf(player, "c_overall"));

        Assert.True(roster.AdjustRatings([player], 200, null).IsSuccess);
        Assert.Equal(RosterFile.HighestRating, roster.RatingOf(player, "c_speed"));
        Assert.Equal(RosterFile.HighestRating, roster.RatingOf(player, "c_overall"));
    }

    [Fact]
    public void ChangesAreListedAndAPlayerCanBeReverted()
    {
        if (Save() is not { } roster || Save() is not { } original)
        {
            return;
        }

        Assert.Empty(roster.ChangesSince(original));

        int team = BusyTeam(roster);
        List<RosterPlayer> players = roster.PlayersOn(team).Where(p => !p.IsGoalie).ToList();
        RosterPlayer player = players[0];
        int speed = roster.RatingOf(player, "c_speed");
        int changedSpeed = speed == RosterFile.HighestRating ? speed - 1 : speed + 1;
        Assert.True(roster.SetName(player, "Test", "Skater").IsSuccess);
        Assert.True(roster.SetRating(player, "c_speed", changedSpeed).IsSuccess);
        Assert.True(roster.SetLineSpot(team, "l1c", roster.LineupOf(team)["l2c"]).IsSuccess);

        IReadOnlyList<RosterChange> changes = roster.ChangesSince(original);
        RosterChange change = changes.Single(c => c.PlayerId == player.PlayerId);
        Assert.Equal("Test Skater", change.Name);
        Assert.Contains($"Speed {speed} → {changedSpeed}", change.What);
        Assert.Contains("Name", change.What);
        Assert.Contains(changes, c => c.PlayerId is null && c.Team == team && c.What == "Lines changed");

        Assert.True(roster.RevertPlayer(original, player.PlayerId).IsSuccess);
        Assert.Equal(speed, roster.RatingOf(player, "c_speed"));
        Assert.Equal(player.LastName, roster.Reread(player)!.LastName);
        Assert.DoesNotContain(roster.ChangesSince(original), c => c.PlayerId == player.PlayerId);
    }

    [Fact]
    public void ASwapIsListedAgainstTheTeam()
    {
        if (Save() is not { } roster || Save() is not { } original)
        {
            return;
        }

        int team = BusyTeam(roster);
        int other = roster.TeamsOnRoster().First(t => t != team && roster.PlayersOn(t).Count >= 18);
        RosterPlayer one = roster.PlayersOn(team).First(p => !p.IsGoalie);
        RosterPlayer two = roster.PlayersOn(other).First(p => !p.IsGoalie && p.PlayerId != one.PlayerId);
        Assert.True(roster.Trade(one, two).IsSuccess);
        Assert.Contains(roster.ChangesSince(original), c => c.PlayerId is null && c.Team == team && c.What.Contains(one.FullName) && c.What.Contains(two.FullName));
    }

    [Fact]
    public void CsvComesBackUnchangedAndAppliesEdits()
    {
        if (Save() is not { } roster || Save() is not { } original)
        {
            return;
        }

        int team = BusyTeam(roster);
        string csv = roster.ExportCsv([team], t => $"Team, {t}");

        var same = roster.ImportCsv(csv);
        Assert.True(same.IsSuccess, same.Error);
        Assert.Equal(0, same.Value!.Values);
        Assert.Empty(roster.ChangesSince(original));

        RosterPlayer player = roster.PlayersOn(team).First(p => !p.IsGoalie);
        string edited = Edit(csv, player, ("LastName", "Csv-Name"), ("Speed", "77"), ("Style", "Grinder"));
        var imported = roster.ImportCsv(edited);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal(1, imported.Value!.Players);
        Assert.Equal("Csv-Name", roster.Reread(player)!.LastName);
        Assert.Equal(77, roster.RatingOf(player, "c_speed"));
        Assert.Equal("Grinder", roster.Reread(player)!.StyleName);
    }

    [Fact]
    public void TwoPlayersCanTradeNumbersInACsvButNotShareOne()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        List<RosterPlayer> players = roster.PlayersOn(team).Take(3).ToList();
        string csv = roster.ExportCsv([team], t => "Team");
        string traded = Edit(Edit(csv, players[0], ("Number", players[1].Jersey.ToString())), players[1], ("Number", players[0].Jersey.ToString()));
        var done = roster.ImportCsv(traded);
        Assert.True(done.IsSuccess, done.Error);
        Assert.Equal(players[1].Jersey, roster.Reread(players[0])!.Jersey);
        Assert.Equal(players[0].Jersey, roster.Reread(players[1])!.Jersey);

        RosterFile fresh = Save()!;
        string clash = Edit(fresh.ExportCsv([team], t => "Team"), players[0], ("Number", players[1].Jersey.ToString()));
        Assert.False(fresh.ImportCsv(clash).IsSuccess);
    }

    [Fact]
    public void BadCsvValuesAreRefusedWithTheLine()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        RosterPlayer player = roster.PlayersOn(team).First(p => !p.IsGoalie);
        string csv = roster.ExportCsv([team], t => "Team");
        var bad = roster.ImportCsv(Edit(csv, player, ("Speed", "150")));
        Assert.False(bad.IsSuccess);
        Assert.Contains("Line", bad.Error);
        Assert.False(roster.ImportCsv("a,b\n1,2").IsSuccess);
        Assert.False(roster.ImportCsv(string.Empty).IsSuccess);

        var unknown = Save()!.ImportCsv("TeamIndex,PlayerId,Speed\n9999,1,50\n");
        Assert.True(unknown.IsSuccess);
        Assert.Single(unknown.Value!.Skipped);
    }

    [Fact]
    public void ASavedRosterOpensAgainWithItsEdits()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        Assert.True(roster.SetBestLines(team).IsSuccess);
        var lineup = roster.LineupOf(team).ToDictionary(p => p.Key, p => p.Value.PlayerId);
        var built = roster.BuildSave();
        Assert.True(built.IsSuccess, built.Error);

        RosterFile again = RosterFile.OpenSave(built.Value!).Value!;
        Assert.Equal(lineup, again.LineupOf(team).ToDictionary(p => p.Key, p => p.Value.PlayerId));
        Assert.Empty(again.ChecksumProblems);
    }

    [Fact]
    public void HistoryStepsBackAndForwardKeepingTheBytes()
    {
        byte[] first = new byte[5000];
        Random.Shared.NextBytes(first);
        byte[] second = (byte[])first.Clone();
        second[10] ^= 1;
        second[4000] ^= 1;
        byte[] third = (byte[])second.Clone();
        third[20] ^= 1;
        byte[] longer = [.. third, 1, 2, 3];

        var history = new RosterHistory();
        Assert.Null(history.Undo(first));
        history.Record("same", first, first);
        Assert.Null(history.UndoDescription);

        history.Record("two", first, second);
        history.Record("three", second, third);
        history.Record("grow", third, longer);
        Assert.Equal("grow", history.UndoDescription);

        Assert.Equal(third, history.Undo(longer));
        Assert.Equal(second, history.Undo(third));
        Assert.Equal("three", history.RedoDescription);
        Assert.Equal(first, history.Undo(second));
        Assert.Null(history.Undo(first));
        Assert.Equal(second, history.Redo(first));
        Assert.Equal(third, history.Redo(second));
        Assert.Equal(longer, history.Redo(third));
        Assert.Null(history.Redo(longer));

        // A new edit after an undo drops what could have been redone.
        Assert.Equal(third, history.Undo(longer));
        history.Record("other", third, first);
        Assert.Null(history.RedoDescription);
    }

    /// <summary>Changes cells on a player's line of an exported CSV.</summary>
    private static string Edit(string csv, RosterPlayer player, params (string Column, string Value)[] edits)
    {
        List<string[]> rows = NhlWorkbench.Core.Database.CsvText.Parse(csv);
        int idColumn = Array.IndexOf(rows[0], "PlayerId");
        string[] row = rows.First(r => r[idColumn] == player.PlayerId.ToString());
        foreach ((string column, string value) in edits)
        {
            row[Array.IndexOf(rows[0], column)] = value;
        }

        return string.Join("\n", rows.Select(r => string.Join(',', r.Select(NhlWorkbench.Core.Database.CsvText.Quote))));
    }

    [Fact]
    public void ChecksFindWhatsWrongWithATeam()
    {
        if (Save() is not { } roster)
        {
            return;
        }

        int team = BusyTeam(roster);
        Assert.DoesNotContain(roster.CheckTeam(team), i => i.Kind is RosterIssueKind.Lines or RosterIssueKind.TeamSize);

        RosterPlayer captain = roster.PlayersOn(team).First(p => p.Captain == RosterFile.Captain);
        Assert.DoesNotContain(roster.CheckTeam(team), i => i.Kind == RosterIssueKind.Letters);
        Assert.True(roster.SetCaptain(captain, RosterFile.NoCaptain).IsSuccess);
        Assert.Contains(roster.CheckTeam(team), i => i.Kind == RosterIssueKind.Letters && i.Text.Contains("Nobody"));

        Assert.Empty(roster.CheckTeam(RosterFile.FreeAgents));
    }
}
