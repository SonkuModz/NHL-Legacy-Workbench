using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.JerseySlots;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class TdbSaveContainerTests
{
    private static string AnchorRoster => Path.Combine(
        SampleFileCatalog.ReferencesRoot, "nhl-modding-studio-sourcecode", "nhl-modding-studio-main", "anchors", "xbox360", "nhllegacy",
        "savefiles", "ROSTER 20260627060052", "ROSTER 20260627060052");

    private static bool IsRosterSave(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            var header = new byte[16];
            return stream.Read(header, 0, 16) == 16 && TdbSaveContainer.LooksLikeContainer(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static IEnumerable<object[]> Rosters()
    {
        if (File.Exists(AnchorRoster))
        {
            yield return new object[] { AnchorRoster };
        }

        // The user's own roster saves (read only here): the live one and its untouched "- Vanilla" copy.
        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "nhllegacy");
        if (Directory.Exists(docs))
        {
            foreach (string f in Directory.EnumerateFiles(docs, "ROSTER *", SearchOption.AllDirectories).Where(IsRosterSave).Take(4))
            {
                yield return new object[] { f };
            }
        }
    }

    [Fact]
    public void Parse_RejectsABareDatabaseAndGarbage()
    {
        Assert.False(TdbSaveContainer.Parse(new byte[200]).IsSuccess);
        if (SampleFileCatalog.VanillaAnchorDatabaseAvailable)
        {
            Assert.False(TdbSaveContainer.LooksLikeContainer(File.ReadAllBytes(SampleFileCatalog.VanillaAnchorDatabaseFile)));
        }
    }

    [Theory]
    [MemberData(nameof(Rosters))]
    public void RealRoster_InflatesToAValidDatabaseWithTheTablesWeEdit(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        Result<TdbSaveContainer> parsed = TdbSaveContainer.Parse(file);
        Assert.True(parsed.IsSuccess, parsed.Error);

        TdbDatabase db = TdbDatabase.Open(parsed.Value!.InnerDatabase).Value!;
        Assert.Empty(db.ChecksumProblems);
        foreach (string id in new[] { TdbDatabase.TeamsTableId, TdbDatabase.EquipmentTableId, TdbDatabase.JerseysTableId })
        {
            Assert.NotNull(db.GetTable(id));
        }
    }

    [Theory]
    [MemberData(nameof(Rosters))]
    public void Rebuild_WithoutEditsKeepsTheDatabaseIdenticalAndTheHeaderConsistent(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        TdbSaveContainer container = TdbSaveContainer.Parse(file).Value!;

        Result<byte[]> rebuilt = container.Build(container.InnerDatabase);
        Assert.True(rebuilt.IsSuccess, rebuilt.Error);
        Assert.Equal(file.Length, rebuilt.Value!.Length);
        Assert.Empty(TdbSaveContainer.VerifyChecksums(rebuilt.Value));
        Assert.Equal(container.InnerDatabase, TdbSaveContainer.Parse(rebuilt.Value).Value!.InnerDatabase);

        // The header's sizes now describe the stream that is really there.
        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(rebuilt.Value.AsSpan(0x24));
        Assert.Equal((uint)(container.InnerDatabase.Length + container.Trailer.Length), declared);
    }

    [Theory]
    [MemberData(nameof(Rosters))]
    public void Rebuild_AfterAddingAJerseySlotRoundTrips(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        TdbSaveContainer container = TdbSaveContainer.Parse(file).Value!;
        TdbDatabase db = TdbDatabase.Open(container.InnerDatabase).Value!;
        TdbTableView jerseys = db.GetTable(TdbDatabase.JerseysTableId)!;
        int before = jerseys.RowCount;
        int source = JerseySlotEditor.RowsForTeam(jerseys, 15)[0];

        Result<int> added = JerseySlotEditor.AddSlot(jerseys, source);
        Assert.True(added.IsSuccess, added.Error);

        Result<byte[]> rebuilt = container.Build(db.ToBytes());
        Assert.True(rebuilt.IsSuccess, rebuilt.Error);
        Assert.Empty(TdbSaveContainer.VerifyChecksums(rebuilt.Value!));

        TdbDatabase reopened = TdbDatabase.Open(TdbSaveContainer.Parse(rebuilt.Value!).Value!.InnerDatabase).Value!;
        Assert.Empty(reopened.ChecksumProblems);
        Assert.Equal(before + 1, reopened.GetTable(TdbDatabase.JerseysTableId)!.RowCount);
    }
}
