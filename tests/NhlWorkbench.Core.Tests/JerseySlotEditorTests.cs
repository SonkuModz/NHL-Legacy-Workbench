using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.JerseySlots;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class JerseySlotEditorTests
{
    public static IEnumerable<object[]> RealDatabases() => TdbFileTests.RealDatabases();

    private static TdbDatabase Open(string path) => TdbDatabase.Open(File.ReadAllBytes(path)).Value!;

    private static void AssertTableInvariants(TdbTableView jerseys)
    {
        long prevTeam = -1;
        long expectedIndex = 0;
        var identities = new HashSet<(long, long, long, long)>();
        for (int r = 0; r < jerseys.RowCount; r++)
        {
            long team = jerseys.GetInt(r, "teamid");
            Assert.True(team >= prevTeam, $"row {r}: teams out of order");
            if (team != prevTeam)
            {
                expectedIndex = 0;
            }

            Assert.Equal(expectedIndex++, jerseys.GetInt(r, "jerseyindex"));
            prevTeam = team;
            if (jerseys.GetInt(r, "teamartid") == 255)
            {
                continue; // the shipped placeholder teams (220/221) all share art id 255 and repeat variant 0
            }

            Assert.True(
                identities.Add((team, jerseys.GetInt(r, "style"), jerseys.GetInt(r, "teamartid"), jerseys.GetInt(r, "variant"))),
                $"row {r}: duplicate (style, art, variant) for the team");
        }
    }

    [Fact]
    public void Naming_MatchesTheGameConventions()
    {
        Assert.Equal("Vintage_0_17_6", JerseySlotEditor.VintageKey(0, 17, 6));
        Assert.Equal("texlib_1_332_4.rx2", JerseySlotEditor.TexlibFileName(1, 332, 4));
        Assert.Equal("texlib_1_9_3.rx2", JerseySlotEditor.PantFileName(1, 9, 3));
        Assert.Equal("texlib_0_9_3.rx2", JerseySlotEditor.PantFileName(0, 9, 3));
        Assert.Equal("sock_1_9_3_cm.rx2", JerseySlotEditor.SockFileName(1, 9, 3));
        Assert.Equal("name_1_0_2_cm.rx2", JerseySlotEditor.NameplateFileName(1, 0, 2));
        Assert.Equal("jersey_1_0_2.big", JerseySlotEditor.MenuArtFileName(1, 0, 2));
        Assert.Equal("_1_0_2", JerseySlotEditor.NameToken(1, 0, 2));
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_ExistingTableAlreadyFollowsTheInvariantsWeRelyOn(string path)
    {
        AssertTableInvariants(Open(path).GetTable(TdbDatabase.JerseysTableId)!);
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void AddSlot_ClonesIntoSpareRowAndKeepsTheTableValid(string path)
    {
        TdbDatabase db = Open(path);
        TdbTableView jerseys = db.GetTable(TdbDatabase.JerseysTableId)!;
        if (jerseys.SpareRows < 1)
        {
            return; // vanilla copy has no spare capacity; nothing to add into
        }

        int before = jerseys.RowCount;
        int teamSlotsBefore = JerseySlotEditor.RowsForTeam(jerseys, 39).Count;
        int source = JerseySlotEditor.RowsForTeam(jerseys, 39)[1];
        long sourceStyle = jerseys.GetInt(source, "style");
        long sourceLight = jerseys.GetInt(source, "islightcolor");

        Result<int> added = JerseySlotEditor.AddSlot(jerseys, source);
        Assert.True(added.IsSuccess, added.Error);
        int row = added.Value;

        Assert.Equal(before + 1, jerseys.RowCount);
        Assert.Equal(teamSlotsBefore + 1, JerseySlotEditor.RowsForTeam(jerseys, 39).Count);
        Assert.Equal(39, jerseys.GetInt(row, "teamid"));
        Assert.Equal(sourceStyle, jerseys.GetInt(row, "style"));
        Assert.Equal(sourceLight, jerseys.GetInt(row, "islightcolor"));
        Assert.Equal(JerseySlotEditor.CustomNameType, jerseys.GetInt(row, "locstring"));
        Assert.Equal(0, jerseys.GetInt(row, "isdefault"));
        Assert.Equal(jerseys.GetInt(row, "variant"), jerseys.GetInt(row, "fevariant"));
        Assert.Equal(jerseys.GetInt(source, "variant").ToString().Length, jerseys.GetInt(row, "variant").ToString().Length); // digit count preserved
        AssertTableInvariants(jerseys);

        byte[] saved = db.ToBytes();
        TdbDatabase reopened = Open2(saved);
        Assert.Empty(reopened.ChecksumProblems);
        Assert.Equal(before + 1, reopened.GetTable(TdbDatabase.JerseysTableId)!.RowCount);
        AssertTableInvariants(reopened.GetTable(TdbDatabase.JerseysTableId)!);
    }

    private static TdbDatabase Open2(byte[] bytes) => TdbDatabase.Open(bytes).Value!;

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void AddSlot_UntilFull_FailsCleanlyWithoutCorruptingAnything(string path)
    {
        TdbDatabase db = Open(path);
        TdbTableView jerseys = db.GetTable(TdbDatabase.JerseysTableId)!;
        int source = JerseySlotEditor.RowsForTeam(jerseys, 0)[0];
        bool sawFailure = false;
        for (int i = 0; i < jerseys.SpareRows + 40; i++)
        {
            int sourceNow = JerseySlotEditor.RowsForTeam(jerseys, 0)[0];
            Result<int> result = JerseySlotEditor.AddSlot(jerseys, sourceNow);
            if (!result.IsSuccess)
            {
                sawFailure = true;
                break;
            }
        }

        Assert.True(sawFailure);
        AssertTableInvariants(jerseys);
        Assert.Empty(Open2(db.ToBytes()).ChecksumProblems);
        Assert.True(source >= 0);
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void Move_SwapsNeighboursKeepsIndexesAndRefusesToCrossTeams(string path)
    {
        TdbTableView jerseys = Open(path).GetTable(TdbDatabase.JerseysTableId)!;
        List<int> rows = JerseySlotEditor.RowsForTeam(jerseys, 39);
        int first = rows[0];
        int second = rows[1];
        long variantFirst = jerseys.GetInt(first, "variant");
        long variantSecond = jerseys.GetInt(second, "variant");

        Assert.True(JerseySlotEditor.TryMove(jerseys, first, +1));
        Assert.Equal(variantSecond, jerseys.GetInt(first, "variant"));
        Assert.Equal(variantFirst, jerseys.GetInt(second, "variant"));
        AssertTableInvariants(jerseys);

        Assert.False(JerseySlotEditor.TryMove(jerseys, rows[0], -1)); // first slot of the team can't move up past the previous team
        Assert.False(JerseySlotEditor.TryMove(jerseys, rows[^1], +1));
    }
}
