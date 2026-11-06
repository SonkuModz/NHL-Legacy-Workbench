using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class GameDatabaseTests
{
    private static readonly string ModSave = Path.Combine(
        SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native", "mods", "26-27 Conversion.zip (1)", "roster", "SYS-DATA");

    private static byte[]? DiscFile(string path)
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return null;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        return game.ReadDecoded(game.Find(path)!.Winner).Value;
    }

    [Theory]
    [InlineData("db/nhlng.db", 134)]
    [InlineData("db/draft_roster.db", 1)]
    [InlineData("db/dynamic_ratings.db", 1)]
    public void EveryDatabaseOpensAndEveryCellReads(string path, int atLeast)
    {
        if (DiscFile(path) is not { } data)
        {
            return;
        }

        GameDatabase db = GameDatabase.Open(data).Value!;
        Assert.True(db.Tables.Count >= atLeast);
        Assert.Empty(db.ChecksumProblems);
        foreach (DatabaseTable table in db.Tables)
        {
            for (int row = 0; row < Math.Min(table.RowCount, 5); row++)
            {
                Assert.All(table.Columns, c => Assert.NotNull(table.GetCell(row, c)));
            }
        }

        // Opening and rebuilding without an edit gives back the same file.
        Assert.Equal(data, db.Build().Value);
    }

    [Fact]
    public void AnEditChangesOnlyThatCellAndSurvivesReopening()
    {
        if (DiscFile("db/nhlng.db") is not { } data)
        {
            return;
        }

        GameDatabase db = GameDatabase.Open(data).Value!;
        DatabaseTable teams = db.FindTable("exhibitionteams")!;
        DatabaseColumn weight = teams.FindColumn("primarycolor_r")!;
        DatabaseColumn name = teams.FindColumn("fullname")!;
        string otherBefore = teams.GetCell(1, weight);

        Assert.Null(teams.SetCell(0, weight, "17"));
        Assert.Null(teams.SetCell(0, name, "Test Team"));

        GameDatabase again = GameDatabase.Open(db.Build().Value!).Value!;
        DatabaseTable reread = again.FindTable("exhibitionteams")!;
        Assert.Equal("17", reread.GetCell(0, reread.FindColumn("primarycolor_r")!));
        Assert.Equal("Test Team", reread.GetCell(0, reread.FindColumn("fullname")!));
        Assert.Equal(otherBefore, reread.GetCell(1, reread.FindColumn("primarycolor_r")!));
        Assert.Empty(again.ChecksumProblems);
    }

    [Fact]
    public void BadCellsAreRefusedAndChangeNothing()
    {
        if (DiscFile("db/nhlng.db") is not { } data)
        {
            return;
        }

        GameDatabase db = GameDatabase.Open(data).Value!;
        DatabaseTable teams = db.FindTable("exhibitionteams")!;
        DatabaseColumn color = teams.FindColumn("primarycolor_r")!;
        DatabaseColumn name = teams.FindColumn("fullname")!;
        string before = teams.GetCell(0, color);

        Assert.NotNull(teams.SetCell(0, color, "abc"));
        Assert.NotNull(teams.SetCell(0, color, "99999"));
        Assert.NotNull(teams.SetCell(0, color, "-5"));
        Assert.NotNull(teams.SetCell(0, name, new string('x', 500)));
        Assert.Equal(before, teams.GetCell(0, color));
    }

    [Fact]
    public void ACsvRoundTripChangesNothing()
    {
        if (DiscFile("db/nhlng.db") is not { } data)
        {
            return;
        }

        GameDatabase db = GameDatabase.Open(data).Value!;
        foreach (string name in new[] { "exhibitionteams", "exhibitionarena", "exhibitionplayerbiotable" })
        {
            DatabaseTable table = db.FindTable(name)!;
            var applied = table.ApplyCsv(table.ToCsv());
            Assert.True(applied.IsSuccess, applied.Error);
            Assert.Equal(0, applied.Value);
        }

        Assert.Equal(data, db.Build().Value);
    }

    [Fact]
    public void ACsvEditAppliesAndABadOneLeavesTheTableAlone()
    {
        if (DiscFile("db/nhlng.db") is not { } data)
        {
            return;
        }

        GameDatabase db = GameDatabase.Open(data).Value!;
        DatabaseTable teams = db.FindTable("exhibitionteams")!;
        string name0 = teams.GetCell(0, teams.FindColumn("fullname")!);
        Assert.True(teams.ApplyCsv("row,fullname,primarycolor_r\r\n0,\"Hello, World\",1\r\n").IsSuccess);
        Assert.Equal("Hello, World", teams.GetCell(0, teams.FindColumn("fullname")!));
        Assert.Equal("1", teams.GetCell(0, teams.FindColumn("primarycolor_r")!));

        var bad = teams.ApplyCsv("row,fullname,primarycolor_r\r\n1,Changed,2\r\n2,Changed,not a number\r\n");
        Assert.False(bad.IsSuccess);
        Assert.Contains("Row 2", bad.Error);
        Assert.NotEqual("Changed", teams.GetCell(1, teams.FindColumn("fullname")!));
        Assert.False(teams.ApplyCsv("row,nothing\r\n0,1\r\n").IsSuccess);
        Assert.False(teams.ApplyCsv("row,fullname\r\n99999,x\r\n").IsSuccess);
        Assert.False(teams.ApplyCsv(string.Empty).IsSuccess);
        Assert.NotEqual(name0, teams.GetCell(0, teams.FindColumn("fullname")!));
    }

    [Fact]
    public void ARosterSaveOpensAndRebuilds()
    {
        if (!File.Exists(ModSave))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(ModSave);
        GameDatabase db = GameDatabase.Open(data).Value!;
        Assert.True(db.IsSave);
        Assert.True(db.Tables.Count >= 39);
        DatabaseTable bios = db.FindTable("exhibitionplayerbiotable")!;
        int row = Enumerable.Range(0, bios.RowCount).First(r => bios.GetCell(r, bios.FindColumn("lastname")!) == "Crosby");
        Assert.Null(bios.SetCell(row, bios.FindColumn("lastname")!, "Changed"));

        var built = db.Build();
        Assert.True(built.IsSuccess, built.Error);
        Assert.Equal(data.Length, built.Value!.Length);
        DatabaseTable reread = GameDatabase.Open(built.Value).Value!.FindTable("exhibitionplayerbiotable")!;
        Assert.Equal("Changed", reread.GetCell(row, reread.FindColumn("lastname")!));
    }

    [Fact]
    public void CsvQuotingFollowsTheUsualRules()
    {
        string[] cells = ["plain", "a,b", "say \"hi\"", "two\nlines", " padded ", string.Empty];
        List<string[]> parsed = CsvText.Parse(string.Join(",", cells.Select(CsvText.Quote)) + "\r\nnext,row\r\n");
        Assert.Equal(cells, parsed[0]);
        Assert.Equal(["next", "row"], parsed[1]);
    }

    [Fact]
    public void GarbageIsRefusedWithoutThrowing()
    {
        Assert.False(GameDatabase.Open([1, 2, 3]).IsSuccess);
        Assert.False(GameDatabase.Open(new byte[5000]).IsSuccess);
    }
}
