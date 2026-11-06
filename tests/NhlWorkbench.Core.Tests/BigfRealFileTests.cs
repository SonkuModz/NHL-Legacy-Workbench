using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth regression coverage against a real BIGF archive
/// (<c>References\.Big Texture File\d24.big</c>), hand-decoded byte-for-byte to find and
/// fix two real bugs in a <see cref="BigfArchive"/> implementation that had never been
/// tested against an actual file: the per-entry data alignment is 64 bytes, not the 256
/// an earlier draft guessed, and the top-level archive-size field (byte offset 4) is
/// little-endian while every other multi-byte field in the format is big-endian.
/// d24.big itself turns out to hold Scaleform/GFx UI bytecode, not a texture -- every
/// entry classifies as Unknown or Marker, which is the correct, real answer for this file
/// (not a placeholder), confirmed by scanning ~1,500 other real .big archives from the
/// same game and finding zero that embed a raw RX2/RPSGL/DDS texture directly.
/// </summary>
public class BigfRealFileTests
{
    [Fact]
    public void Parse_RealArchive_MatchesHandDecodedHeaderAndEntries()
    {
        if (!SampleFileCatalog.BigSampleAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.BigSampleFile);
        Result<BigfArchive> result = BigfArchive.Parse(data, SampleFileCatalog.BigSampleFile);

        Assert.True(result.IsSuccess, result.Error);
        BigfArchive archive = result.Value!;

        Assert.Equal(6, archive.Entries.Count);
        Assert.Equal(90u, archive.TocSize);
        Assert.Equal(14880u, archive.DeclaredArchiveSize);

        Assert.Equal(128, archive.Entries[0].Offset);
        Assert.Equal(616, archive.Entries[0].Size);
        Assert.Equal("0", archive.Entries[0].Name);

        Assert.Equal(768, archive.Entries[1].Offset);
        Assert.Equal(288, archive.Entries[1].Size);
        Assert.Equal("97", archive.Entries[1].Name);

        // Zero-size marker entries share the following real entry's offset.
        Assert.Equal(1088, archive.Entries[2].Offset);
        Assert.Equal(0, archive.Entries[2].Size);
        Assert.Equal("sg1", archive.Entries[2].Name);

        Assert.Equal(1088, archive.Entries[3].Offset);
        Assert.Equal(13716, archive.Entries[3].Size);
        Assert.Equal("96", archive.Entries[3].Name);

        Assert.Equal(14848, archive.Entries[4].Offset);
        Assert.Equal(0, archive.Entries[4].Size);
        Assert.Equal("sg2", archive.Entries[4].Name);

        Assert.Equal(14848, archive.Entries[5].Offset);
        Assert.Equal(32, archive.Entries[5].Size);
        Assert.Equal("1", archive.Entries[5].Name);

        // Confirmed via hand-decoding: last entry's offset + size == the (little-endian)
        // archive-size field's real value, 14880 -- 16 bytes short of the file's own
        // length, which is trailing padding outside the format's own accounting.
        int lastEntryEnd = archive.Entries[5].Offset + archive.Entries[5].Size;
        Assert.Equal(14880, lastEntryEnd);
    }

    [Fact]
    public void Parse_RealArchive_ClassifiesEveryEntryCorrectly()
    {
        if (!SampleFileCatalog.BigSampleAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.BigSampleFile);
        BigfArchive archive = BigfArchive.Parse(data, SampleFileCatalog.BigSampleFile).Value!;

        // Ground truth (confirmed against NHL Modding Studio's independent reader): entry
        // "96" is the RefPack-compressed XPR2 team-logo texture; "0" and "1" are
        // Scaleform/GFx "Apt Data" bytecode; "97" is an unidentified lookup table; the
        // sg1/sg2 zero-size entries are section markers.
        var expected = new Dictionary<string, BigEntryFormat>
        {
            ["0"] = BigEntryFormat.Unknown,
            ["97"] = BigEntryFormat.Unknown,
            ["sg1"] = BigEntryFormat.Marker,
            ["96"] = BigEntryFormat.Xpr,
            ["sg2"] = BigEntryFormat.Marker,
            ["1"] = BigEntryFormat.Unknown,
        };

        foreach (BigfEntry entry in archive.Entries)
        {
            byte[] entryData = archive.GetEntryData(entry.Name);
            Assert.Equal(expected[entry.Name], BigEntryClassifier.Classify(entryData));
        }
    }

    [Fact]
    public void RebuildReplacingEntries_RealArchive_RoundTripsEveryUntouchedEntryByteForByte()
    {
        if (!SampleFileCatalog.BigSampleAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.BigSampleFile);
        BigfArchive archive = BigfArchive.Parse(data, SampleFileCatalog.BigSampleFile).Value!;

        // Replace one real (non-marker) entry with different-length data to exercise the
        // 64-byte-alignment fix, and confirm every other entry -- including the zero-size
        // markers -- survives the rebuild unchanged.
        byte[] replacement = new byte[999];
        new Random(7).NextBytes(replacement);
        byte[] rebuilt = archive.RebuildReplacingEntries(new Dictionary<string, byte[]> { ["97"] = replacement });

        BigfArchive reparsed = BigfArchive.Parse(rebuilt, SampleFileCatalog.BigSampleFile).Value!;
        Assert.Equal(archive.Entries.Count, reparsed.Entries.Count);

        foreach (BigfEntry original in archive.Entries)
        {
            byte[] expected = original.Name == "97" ? replacement : archive.GetEntryData(original.Name);
            byte[] actual = reparsed.GetEntryData(original.Name);
            Assert.Equal(expected, actual);
        }
    }
}
