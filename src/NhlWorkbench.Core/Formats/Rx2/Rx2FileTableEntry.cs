using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>A single 24-byte RX2 file-table entry, read at a known absolute offset.</summary>
internal readonly struct Rx2FileTableEntry
{
    public int OffsetField { get; }

    public int SizeField { get; }

    public int AlignmentField { get; }

    public uint TypeTag { get; }

    private Rx2FileTableEntry(int offsetField, int sizeField, int alignmentField, uint typeTag)
    {
        OffsetField = offsetField;
        SizeField = sizeField;
        AlignmentField = alignmentField;
        TypeTag = typeTag;
    }

    public static Rx2FileTableEntry ReadAt(byte[] data, int entryOffset) =>
        new(
            BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.EntryOffsetField),
            BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.EntrySizeField),
            BigEndianReader.ReadInt32BEAt(data, entryOffset + Rx2HeaderLayout.EntryAlignmentField),
            BigEndianReader.ReadUInt32BEAt(data, entryOffset + Rx2HeaderLayout.EntryTypeTagField));
}
