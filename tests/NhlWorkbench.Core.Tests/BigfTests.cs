using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Synthetic-archive round-trip coverage (arbitrary entry names/sizes, not tied to any
/// specific real file). For byte-layout assertions grounded against an actual BIGF
/// archive, see <c>BigfRealFileTests</c>.
/// </summary>
public class BigfTests
{
    [Fact]
    public void ParseThenRebuildReplacingEntry_RoundTrips()
    {
        byte[] entryA = Encoding.ASCII.GetBytes("Hello world, this is entry A's contents.");
        var entryB = new byte[500];
        new Random(42).NextBytes(entryB);

        byte[] archiveBytes = BuildSyntheticBigf(new (string Name, byte[] Data)[]
        {
            ("alpha.txt", entryA),
            ("beta.bin", entryB),
        });

        Result<BigfArchive> parsed = BigfArchive.Parse(archiveBytes, "synthetic.big");
        Assert.True(parsed.IsSuccess, parsed.Error);
        BigfArchive archive = parsed.Value!;

        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(entryA, archive.GetEntryData("alpha.txt"));
        Assert.Equal(entryB, archive.GetEntryData("beta.bin"));

        byte[] replacement = Encoding.ASCII.GetBytes("Replaced contents for alpha, a different length than before.");
        byte[] rebuilt = archive.RebuildReplacingEntry("alpha.txt", replacement);

        Result<BigfArchive> reparsed = BigfArchive.Parse(rebuilt, "synthetic.big");
        Assert.True(reparsed.IsSuccess, reparsed.Error);
        Assert.Equal(replacement, reparsed.Value!.GetEntryData("alpha.txt"));
        Assert.Equal(entryB, reparsed.Value!.GetEntryData("beta.bin"));
    }

    private static byte[] BuildSyntheticBigf((string Name, byte[] Data)[] entries)
    {
        int tocSize = 16 + entries.Sum(e => 8 + e.Name.Length + 1);
        int dataStart = AlignUp(tocSize, 64);

        var offsets = new int[entries.Length];
        int cursor = dataStart;
        for (int i = 0; i < entries.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignUp(entries[i].Data.Length, 64);
        }

        using var stream = new MemoryStream();

        void WriteU32BE(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            stream.Write(buffer);
        }

        stream.Write("BIGF"u8);
        WriteU32BE((uint)cursor);
        WriteU32BE((uint)entries.Length);
        WriteU32BE((uint)tocSize);

        for (int i = 0; i < entries.Length; i++)
        {
            WriteU32BE((uint)offsets[i]);
            WriteU32BE((uint)entries[i].Data.Length);
            stream.Write(Encoding.ASCII.GetBytes(entries[i].Name));
            stream.WriteByte(0);
        }

        stream.SetLength(cursor);

        for (int i = 0; i < entries.Length; i++)
        {
            stream.Position = offsets[i];
            stream.Write(entries[i].Data);
        }

        return stream.ToArray();
    }

    private static int AlignUp(int value, int alignment) => ((value + alignment - 1) / alignment) * alignment;
}
