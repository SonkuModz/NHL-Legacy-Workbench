using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Bigf;

/// <summary>
/// Read-only access plus rebuild-with-replacements for the outer BIGF archive format that
/// optionally wraps RX2/RPSGL/DDS payloads.
///
/// Byte layout ground-truthed against a real sample (<c>References\.Big Texture File\d24.big</c>,
/// see <c>BigfRealFileTests</c>): magic "BIGF"; fileCount (u32 BE, byte 8), TOC size (u32 BE,
/// byte 12), and every per-entry offset/size (u32 BE) all confirmed big-endian by hand-decoding
/// the real header and file table. The one exception is the top-level archive-size field at byte
/// 4, which is little-endian -- confirmed by that same file (BE-decoding it gives a nonsensical
/// value; LE gives exactly "last entry's offset + size"). Entry data starts at
/// <c>AlignUp(tocSize, 64)</c> with no extra minimum -- the real sample's first entry starts at
/// byte 0x80 (128), below what an earlier, unverified draft of this class assumed was a 256-byte
/// minimum. Zero-size entries are pure section-label markers that share the following real
/// entry's offset rather than reserving any aligned space of their own; the alignment math
/// already produces this for free via <c>AlignUp(0, N) == 0</c>.
/// </summary>
public sealed class BigfArchive
{
    private const int TocEntryFixedSize = 8; // offset (4) + size (4); name is variable + null terminator
    private const int DataAlignment = 64;

    private readonly byte[] _data;

    private BigfArchive(byte[] data, string sourcePath, IReadOnlyList<BigfEntry> entries, uint declaredArchiveSize, uint tocSize)
    {
        _data = data;
        SourcePath = sourcePath;
        Entries = entries;
        DeclaredArchiveSize = declaredArchiveSize;
        TocSize = tocSize;
    }

    public string SourcePath { get; }

    public IReadOnlyList<BigfEntry> Entries { get; }

    /// <summary>The header's little-endian archive-size field (byte offset 4) -- see the class remarks. Developer-panel/diagnostic use only; nothing in this reader validates against it.</summary>
    public uint DeclaredArchiveSize { get; }

    /// <summary>The header's TOC-size field (byte offset 12, big-endian) -- the byte offset where entry data starts is <c>AlignUp(TocSize, 64)</c>.</summary>
    public uint TocSize { get; }

    public static bool IsBigf(byte[] data) =>
        data.Length >= 16 && data[0] == (byte)'B' && data[1] == (byte)'I' && data[2] == (byte)'G' && data[3] == (byte)'F';

    /// <summary>True for BIGF and for BIG4, which shares its layout. <see cref="IsBigf"/> stays BIGF-only because only BIGF can be rebuilt.</summary>
    public static bool IsBigfOrBig4(byte[] data) =>
        IsBigf(data) || (data.Length >= 16 && data.AsSpan(0, 4).SequenceEqual("BIG4"u8));

    public static Result<BigfArchive> Parse(byte[] data, string sourcePath)
    {
        try
        {
            if (!IsBigfOrBig4(data))
            {
                return Result.Failure<BigfArchive>("Not a BIGF or BIG4 archive (magic mismatch).");
            }

            var warnings = new List<ParseWarning>();
            uint declaredArchiveSize = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
            uint fileCount = BigEndianReader.ReadUInt32BEAt(data, 8);
            uint tocSize = BigEndianReader.ReadUInt32BEAt(data, 12);

            var entries = new List<BigfEntry>();
            int cursor = 16;

            for (int i = 0; i < fileCount; i++)
            {
                if (cursor + TocEntryFixedSize > data.Length)
                {
                    warnings.Add(new ParseWarning($"TOC entry {i} is out of range; stopping scan early.", "BigfArchive"));
                    break;
                }

                int offset = BigEndianReader.ReadInt32BEAt(data, cursor);
                int size = BigEndianReader.ReadInt32BEAt(data, cursor + 4);
                cursor += TocEntryFixedSize;

                string name = BigEndianReader.ReadNullTerminatedAsciiAt(data, cursor);
                cursor += name.Length + 1;

                entries.Add(new BigfEntry(name, offset, size));
            }

            return Result.Success(new BigfArchive(data, sourcePath, entries, declaredArchiveSize, tocSize), warnings);
        }
        catch (Exception ex)
        {
            return Result.Failure<BigfArchive>($"Unhandled error parsing BIGF archive: {ex.Message}", ex);
        }
    }

    public byte[] GetEntryData(string name)
    {
        BigfEntry? entry = Entries.FirstOrDefault(e => e.Name == name);
        if (entry is null)
        {
            throw new ArgumentException($"No entry named '{name}' in this archive.", nameof(name));
        }

        return BufferUtilities.SafeSlice(_data, entry.Offset, entry.Size);
    }

    /// <summary>Rebuilds the archive with exactly one named entry's data replaced; every other entry's bytes are carried over unchanged.</summary>
    public byte[] RebuildReplacingEntry(string name, byte[] newData) =>
        RebuildReplacingEntries(new Dictionary<string, byte[]> { [name] = newData });

    /// <summary>Rebuilds the archive with every named entry in <paramref name="replacements"/> replaced in one pass; every other entry's bytes are carried over unchanged.</summary>
    public byte[] RebuildReplacingEntries(IReadOnlyDictionary<string, byte[]> replacements)
    {
        if (!IsBigf(_data))
        {
            throw new NotSupportedException("Only BIGF archives can be rebuilt; this one is BIG4.");
        }

        foreach (string name in replacements.Keys)
        {
            if (Entries.All(e => e.Name != name))
            {
                throw new ArgumentException($"No entry named '{name}' in this archive.", nameof(replacements));
            }
        }

        int tocSize = 16 + Entries.Sum(e => TocEntryFixedSize + e.Name.Length + 1);
        int dataStart = AlignUp(tocSize, DataAlignment);

        var entryData = new byte[Entries.Count][];
        var entryOffsets = new int[Entries.Count];
        int cursor = dataStart;

        for (int i = 0; i < Entries.Count; i++)
        {
            BigfEntry entry = Entries[i];
            entryData[i] = replacements.TryGetValue(entry.Name, out byte[]? replacement) ? replacement : GetEntryData(entry.Name);
            entryOffsets[i] = cursor;
            cursor += AlignUp(entryData[i].Length, DataAlignment);
        }

        var writer = new BigEndianWriter();
        writer.WriteBytes("BIGF"u8);
        writer.WriteUInt32BE(0); // patched below (little-endian) once the total size is known
        writer.WriteUInt32BE((uint)Entries.Count);
        writer.WriteUInt32BE((uint)tocSize);

        for (int i = 0; i < Entries.Count; i++)
        {
            writer.WriteUInt32BE((uint)entryOffsets[i]);
            writer.WriteUInt32BE((uint)entryData[i].Length);
            writer.WriteNullTerminatedAscii(Entries[i].Name);
        }

        while (writer.Position < dataStart)
        {
            writer.WriteByte(0);
        }

        foreach (byte[] data in entryData)
        {
            writer.WriteBytes(data);
            writer.PadTo(DataAlignment);
        }

        writer.WriteUInt32LEAt(4, (uint)writer.Length);

        return writer.ToArray();
    }

    private static int AlignUp(int value, int alignment) => ((value + alignment - 1) / alignment) * alignment;
}
