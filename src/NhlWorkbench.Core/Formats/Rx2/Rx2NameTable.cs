using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>Parses the RX2 name table (u16 count pair, padding, N 12-byte entries, then null-terminated strings).</summary>
internal static class Rx2NameTable
{
    public static string[]? TryParse(byte[] data, int nameTableOffset, List<ParseWarning> warnings)
    {
        if (nameTableOffset < 0 || nameTableOffset + Rx2HeaderLayout.NameTableEntriesStartOffset > data.Length)
        {
            warnings.Add(new ParseWarning("Name table offset out of range; falling back to synthetic names.", "Rx2NameTable"));
            return null;
        }

        ushort countB = BigEndianReader.ReadUInt16BEAt(data, nameTableOffset + Rx2HeaderLayout.NameTableCountBOffset);
        if (countB == 0)
        {
            return null;
        }

        var names = new string[countB];
        int entriesBase = nameTableOffset + Rx2HeaderLayout.NameTableEntriesStartOffset;

        for (int i = 0; i < countB; i++)
        {
            int entryOffset = entriesBase + (i * Rx2HeaderLayout.NameTableEntrySize);
            if (!TryResolveEntryName(data, nameTableOffset, entryOffset, i, warnings, out string name))
            {
                name = i.ToString();
            }

            names[i] = name;
        }

        return names;
    }

    /// <summary>
    /// Parses a name table the same way <see cref="TryParse"/> does, but keys each resolved
    /// name by the entry's own explicit target file-table index (<see
    /// cref="Rx2HeaderLayout.NameEntryTextureIndexOffset"/>) rather than by the entry's
    /// position within the table. The mesh format needs this: unlike texture containers
    /// (where name-table position happens to line up with texture index), mesh name-table
    /// entries can name an arbitrary file-table entry -- e.g. a material -- identified by that
    /// explicit index, per the reference importer (`fileList[nameIndex][6] = name`).
    /// Unlike <see cref="TryParse"/>, the reference importer never branches on the entry's
    /// second field for this use -- it always treats the name offset as absolute (confirmed
    /// against real stadium/ice files, where treating it as absolute yields real readable names
    /// and treating it as a type discriminator finds tag values that aren't actually
    /// Absolute/Relative at all) -- so this method resolves unconditionally-absolute.
    /// </summary>
    public static void MergeParseByTargetIndex(byte[] data, int nameTableOffset, Dictionary<int, string> namesByTargetIndex, List<ParseWarning> warnings)
    {
        if (nameTableOffset < 0 || nameTableOffset + Rx2HeaderLayout.NameTableEntriesStartOffset > data.Length)
        {
            warnings.Add(new ParseWarning("Name table offset out of range.", "Rx2NameTable"));
            return;
        }

        ushort countB = BigEndianReader.ReadUInt16BEAt(data, nameTableOffset + Rx2HeaderLayout.NameTableCountBOffset);
        int entriesBase = nameTableOffset + Rx2HeaderLayout.NameTableEntriesStartOffset;

        for (int i = 0; i < countB; i++)
        {
            int entryOffset = entriesBase + (i * Rx2HeaderLayout.NameTableEntrySize);
            if (entryOffset + Rx2HeaderLayout.NameTableEntrySize > data.Length)
            {
                warnings.Add(new ParseWarning($"Name table entry {i} is out of range; skipping.", "Rx2NameTable"));
                continue;
            }

            int nameAddr = BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.NameEntryAddressOffset);
            int targetIndex = BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.NameEntryTextureIndexOffset);

            if (nameAddr < 0 || nameAddr >= data.Length)
            {
                warnings.Add(new ParseWarning($"Name table entry {i} resolves out of range; skipping.", "Rx2NameTable"));
                continue;
            }

            namesByTargetIndex[targetIndex] = BigEndianReader.ReadNullTerminatedAsciiAt(data, nameAddr);
        }
    }

    private static bool TryResolveEntryName(byte[] data, int nameTableOffset, int entryOffset, int entryIndex, List<ParseWarning> warnings, out string name)
    {
        name = string.Empty;
        if (entryOffset + Rx2HeaderLayout.NameTableEntrySize > data.Length)
        {
            warnings.Add(new ParseWarning($"Name table entry {entryIndex} is out of range.", "Rx2NameTable"));
            return false;
        }

        int nameAddr = BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.NameEntryAddressOffset);
        uint nameType = BigEndianReader.ReadUInt32BEAt(data, entryOffset + Rx2HeaderLayout.NameEntryTypeTagOffset);

        int resolvedAddr;
        if (nameType == Rx2HeaderLayout.NameTypeAbsolute)
        {
            resolvedAddr = nameAddr;
        }
        else if (nameType == Rx2HeaderLayout.NameTypeRelative)
        {
            resolvedAddr = nameTableOffset + nameAddr;
        }
        else
        {
            warnings.Add(new ParseWarning($"Name table entry {entryIndex} has an unrecognized type tag 0x{nameType:X8}.", "Rx2NameTable"));
            return false;
        }

        if (resolvedAddr < 0 || resolvedAddr >= data.Length)
        {
            warnings.Add(new ParseWarning($"Name table entry {entryIndex} resolves out of range.", "Rx2NameTable"));
            return false;
        }

        name = BigEndianReader.ReadNullTerminatedAsciiAt(data, resolvedAddr);
        return true;
    }
}
