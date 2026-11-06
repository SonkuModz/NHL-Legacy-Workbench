using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class TdbLanguageFileTests
{
    // nhl_lng_lg.db is a different, unsorted table (not the key/text strings) -- covered separately below.
    public static IEnumerable<object[]> RealLanguageDatabases() => SampleFileCatalog.LanguageDatabaseFiles
        .Where(f => !f.EndsWith("nhl_lng_lg.db", StringComparison.OrdinalIgnoreCase))
        .Select(f => new object[] { f });

    [Fact]
    public void NonStringTableLanguageFile_IsRejectedNotMisread()
    {
        string? lng = SampleFileCatalog.LanguageDatabaseFiles.FirstOrDefault(f => f.EndsWith("nhl_lng_lg.db", StringComparison.OrdinalIgnoreCase));
        if (lng is null)
        {
            return;
        }

        Assert.False(TdbLanguageFile.Parse(File.ReadAllBytes(lng)).IsSuccess);
    }

    private static string EnglishPath => SampleFileCatalog.LanguageDatabaseFiles.FirstOrDefault(f => f.EndsWith("nhl_eng_us.db", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    [Fact]
    public void HashKey_IsCaseInsensitiveAndMatchesKnownRealRows()
    {
        Assert.Equal(TdbLanguageFile.HashKey("Vintage_0_17_6"), TdbLanguageFile.HashKey("VINTAGE_0_17_6"));
        Assert.Equal(TdbLanguageFile.HashKey("vintage_0_17_6"), TdbLanguageFile.HashKey("VINTAGE_0_17_6"));

        // Real (key, hash) pairs from nhl_eng_us.db, including a digit-bearing key and a mixed-case key.
        Assert.Equal(82862u, TdbLanguageFile.HashKey("TXT_BOTH"));
        Assert.Equal(392140u, TdbLanguageFile.HashKey("SB_GK_SPEED_MED1"));
        Assert.Equal(1469717u, TdbLanguageFile.HashKey("ChangeDay"));
    }

    [Theory]
    [MemberData(nameof(RealLanguageDatabases))]
    public void RealLanguageDatabase_EveryRowHashMatchesItsKey_AndNoOpResaveIsByteIdentical(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        Result<TdbLanguageFile> parsed = TdbLanguageFile.Parse(original);
        Assert.True(parsed.IsSuccess, parsed.Error);

        int checkedRows = 0;
        foreach ((string key, string _) in parsed.Value!.Entries())
        {
            Assert.True(parsed.Value.ContainsKey(key), $"hash of '{key}' doesn't locate its own row");
            checkedRows++;
        }

        Assert.Equal(parsed.Value.Count, checkedRows);

        byte[] rebuilt = parsed.Value.ToBytes();
        if (parsed.Value.NeedsPoolEndRepair)
        {
            // A file an older build of this tool wrote strings past the pool end of: saving must repair it (and then be stable).
            TdbLanguageFile repaired = TdbLanguageFile.Parse(rebuilt).Value!;
            Assert.False(repaired.NeedsPoolEndRepair);
            Assert.Equal(rebuilt, repaired.ToBytes());
        }
        else
        {
            Assert.Equal(original, rebuilt);
        }
    }

    [Fact]
    public void SetAndAdd_PersistRoundTripKeepEverythingElseAndStayValid()
    {
        if (EnglishPath.Length == 0)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(EnglishPath);
        TdbLanguageFile file = TdbLanguageFile.Parse(original).Value!;
        Dictionary<string, string> before = file.Entries().ToDictionary(e => e.Key.ToUpperInvariant(), e => e.Text);

        const string existingKey = "Vintage_0_17_6";
        Assert.True(file.TryGetText(existingKey, out string oldName));
        Assert.Null(file.TrySetText(existingKey, "Renamed Jersey (Edited)"));
        Assert.Null(file.TrySetText("Vintage_1_999_31", "My Brand New Jersey \u00AE"));
        Assert.NotNull(file.TrySetText("Vintage_1_999_30", "pipe | char")); // '|' never occurs in the English strings, so the tree has no code for it: refused, nothing changed
        Assert.False(file.ContainsKey("Vintage_1_999_30"));

        byte[] saved = file.ToBytes();
        Assert.True(saved.Length > original.Length);

        TdbLanguageFile reopened = TdbLanguageFile.Parse(saved).Value!;
        Assert.Equal(before.Count + 1, reopened.Count);
        Assert.True(reopened.TryGetText(existingKey, out string newName));
        Assert.Equal("Renamed Jersey (Edited)", newName);
        Assert.NotEqual(oldName, newName);
        Assert.True(reopened.TryGetText("VINTAGE_1_999_31", out string added)); // case-insensitive lookup
        Assert.Equal("My Brand New Jersey \u00AE", added);

        // Every other string is unchanged.
        foreach ((string key, string text) in reopened.Entries())
        {
            string upper = key.ToUpperInvariant();
            if (upper is "VINTAGE_0_17_6" or "VINTAGE_1_999_31")
            {
                continue;
            }

            Assert.Equal(before[upper], text);
        }

        Assert.Empty(TdbFile.Parse(saved).Value!.VerifyChecksums());

        // The game stops reading the pool at the end marker stored in the table header, so every string we appended
        // must lie before it (this is what made new names show as "aaaaaIrXatsCe..." in game).
        Assert.False(reopened.NeedsPoolEndRepair);
        Assert.True(reopened.PoolLength > TdbLanguageFile.Parse(original).Value!.PoolLength);
    }

    [Theory]
    [MemberData(nameof(RealLanguageDatabases))]
    public void RealLanguageDatabase_DeclaredPoolEndNeverFallsShortOfAnyString(string path)
    {
        TdbLanguageFile file = TdbLanguageFile.Parse(File.ReadAllBytes(path)).Value!;
        // After Parse the pool end already accounts for every string, so a save can never leave one beyond it.
        TdbLanguageFile reparsed = TdbLanguageFile.Parse(file.ToBytes()).Value!;
        Assert.False(reparsed.NeedsPoolEndRepair);
        Assert.Equal(file.PoolLength, reparsed.PoolLength);
    }
}
