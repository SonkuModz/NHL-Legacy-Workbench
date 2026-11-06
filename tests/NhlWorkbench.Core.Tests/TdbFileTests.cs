using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class TdbFileTests
{
    [Fact]
    public void Crc_MatchesKnownAnswer()
    {
        // CRC-32/MPEG-2 check value from the NHL Modding Studio spec.
        Assert.Equal(0x0376E6E7u, TdbCrc.Compute("123456789"u8));
    }

    [Fact]
    public void Bits_RoundTripAcrossByteBoundaries()
    {
        var buffer = new byte[8];
        TdbBits.Write(buffer, 5, 14, 0x2ABC & 0x3FFF);
        TdbBits.Write(buffer, 27, 3, 5);

        Assert.Equal((ulong)(0x2ABC & 0x3FFF), TdbBits.Read(buffer, 5, 14));
        Assert.Equal(5UL, TdbBits.Read(buffer, 27, 3));
        Assert.Equal(0UL, TdbBits.Read(buffer, 0, 5)); // neighbours untouched
    }

    [Fact]
    public void Parse_RejectsGarbageWithoutThrowing()
    {
        Assert.False(TdbFile.Parse(new byte[100]).IsSuccess);
        Assert.False(TdbFile.Parse(Array.Empty<byte>()).IsSuccess);
    }

    public static IEnumerable<object[]> RealDatabases()
    {
        if (SampleFileCatalog.GameDatabaseAvailable)
        {
            yield return new object[] { SampleFileCatalog.GameDatabaseFile };
        }

        if (SampleFileCatalog.VanillaAnchorDatabaseAvailable)
        {
            yield return new object[] { SampleFileCatalog.VanillaAnchorDatabaseFile };
        }
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_StoredChecksumsAreAllConsistent_AndNoOpResaveIsByteIdentical(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        Result<TdbFile> parsed = TdbFile.Parse(original);
        Assert.True(parsed.IsSuccess, parsed.Error);

        IReadOnlyList<string> mismatches = parsed.Value!.VerifyChecksums();
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches.Take(5)));
        Assert.Equal(original, parsed.Value!.ToBytes());
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_EditedTablesMatchTheBundledSchema(string path)
    {
        Result<TdbDatabase> db = TdbDatabase.Open(File.ReadAllBytes(path));
        Assert.True(db.IsSuccess, db.Error);

        foreach (string id in new[] { TdbDatabase.TeamsTableId, TdbDatabase.EquipmentTableId, TdbDatabase.JerseysTableId })
        {
            Assert.NotNull(db.Value!.GetTable(id));
        }

        // Only the tables the editor writes need to agree; other tables can differ without risk.
        string[] editedNames = { "exhibitionteams", "stockteamequipmenttable", "stockteamjerseys" };
        string[] relevant = db.Value!.SchemaProblems.Where(p => editedNames.Any(n => p.StartsWith(n + ".", StringComparison.Ordinal))).ToArray();
        Assert.True(relevant.Length == 0, string.Join("; ", relevant));
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_EditPersistsAndChecksumsStayValid(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        TdbDatabase db = TdbDatabase.Open(original).Value!;
        TdbTableView equipment = db.GetTable(TdbDatabase.EquipmentTableId)!;
        Assert.True(equipment.RowCount > 10);

        long before = equipment.GetInt(7, "zone1_r");
        long after = before == 200 ? 100 : 200;
        Assert.True(equipment.TrySetInt(7, "zone1_r", after, out string? error), error);

        // Out-of-range and wrong-field writes are refused, not clamped.
        Assert.False(equipment.TrySetInt(7, "zone1_r", 256, out _));
        Assert.False(equipment.TrySetInt(7, "zone1_r", -1, out _));

        byte[] saved = db.ToBytes();
        Assert.Equal(original.Length, saved.Length);

        TdbDatabase reopened = TdbDatabase.Open(saved).Value!;
        Assert.Empty(reopened.ChecksumProblems);
        Assert.Equal(after, reopened.GetTable(TdbDatabase.EquipmentTableId)!.GetInt(7, "zone1_r"));

        // Neighbouring rows are untouched.
        TdbTableView untouched = TdbDatabase.Open(original).Value!.GetTable(TdbDatabase.EquipmentTableId)!;
        TdbTableView edited = reopened.GetTable(TdbDatabase.EquipmentTableId)!;
        for (int row = 0; row < untouched.RowCount; row++)
        {
            if (row == 7)
            {
                continue;
            }

            Assert.Equal(untouched.GetInt(row, "zone1_r"), edited.GetInt(row, "zone1_r"));
            Assert.Equal(untouched.GetInt(row, "brand"), edited.GetInt(row, "brand"));
        }
    }

    private static TdbFieldMeta Meta(int depth, long low, long high) =>
        new() { Id = "test", Name = "test", IsText = false, Depth = depth, RangeLow = low, RangeHigh = high };

    [Fact]
    public void FieldMeta_TwoComplementRange_DecodesAndEncodesSignedValues()
    {
        TdbFieldMeta m = Meta(8, -50, 50); // spec worked vector: raw 246 = -10, raw 206 = -50
        Assert.Equal(-10, m.Decode(246));
        Assert.Equal(-50, m.Decode(206));
        Assert.Equal(50, m.Decode(50));
        Assert.True(m.TryEncode(-10, out ulong raw));
        Assert.Equal(246UL, raw);
        Assert.False(m.TryEncode(51, out _));
    }

    [Fact]
    public void FieldMeta_NullSentinelRange_TreatsAllOnesAsMinusOneNotTwosComplement()
    {
        TdbFieldMeta equipment = Meta(4, -1, 14);
        Assert.Equal(8, equipment.Decode(8)); // helmets must not become -8
        Assert.Equal(14, equipment.Decode(14));
        Assert.Equal(-1, equipment.Decode(15));
        Assert.True(equipment.TryEncode(-1, out ulong raw));
        Assert.Equal(15UL, raw);

        TdbFieldMeta brand = Meta(8, -1, 254);
        Assert.Equal(200, brand.Decode(200));
        Assert.Equal(-1, brand.Decode(255));
        Assert.True(brand.TryEncode(200, out raw));
        Assert.Equal(200UL, raw);
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_EquipmentTypesDecodeToValidKnownRange(string path)
    {
        TdbTableView equipment = TdbDatabase.Open(File.ReadAllBytes(path)).Value!.GetTable(TdbDatabase.EquipmentTableId)!;
        var types = Enumerable.Range(0, equipment.RowCount).Select(r => equipment.GetInt(r, "equipment")).ToHashSet();

        Assert.All(types, t => Assert.InRange(t, -1, 14));
        Assert.Contains(5L, types);  // gloves
        Assert.Contains(8L, types);  // helmets
    }

    [Theory]
    [MemberData(nameof(RealDatabases))]
    public void RealDatabase_TeamNamesAndSignedFieldsDecode(string path)
    {
        TdbDatabase db = TdbDatabase.Open(File.ReadAllBytes(path)).Value!;
        TdbTableView teams = db.GetTable(TdbDatabase.TeamsTableId)!;

        var names = Enumerable.Range(0, teams.RowCount).Select(r => teams.GetText(r, "fullname")).Where(n => n.Length > 0).ToList();
        Assert.True(names.Count > 30, $"only {names.Count} named teams");
        Assert.All(names, n => Assert.DoesNotContain('�', n));

        // helmetbrand is -1..254 -- -1 must decode as a negative number, not 255.
        long min = Enumerable.Range(0, teams.RowCount).Min(r => teams.GetInt(r, "helmetbrand"));
        Assert.InRange(min, -1, 254);
    }
}
