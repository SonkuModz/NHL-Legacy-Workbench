namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Named byte offsets for the RX2 (Xbox 360) container header. Every offset here is
/// cross-validated against both the working Python prototype and the shipped Noesis
/// Python plugin for this format -- no inline magic numbers should appear anywhere else
/// in the RX2 reader/writer.
/// </summary>
public static class Rx2HeaderLayout
{
    // NOTE: cannot use a u8 string literal here -- 0x89 is outside ASCII, so a UTF-8
    // literal would encode it as the two bytes 0xC2 0x89 instead of the single raw byte
    // the file format actually uses.
    public static readonly byte[] Magic = { 0x89, (byte)'R', (byte)'W', (byte)'4', (byte)'x', (byte)'b', (byte)'2' };

    public const int MagicLength = 7;

    /// <summary>3-char version string, e.g. "454".</summary>
    public const int VersionOffset = 0x10;

    public const int VersionLength = 3;

    /// <summary>Number of entries in the file table (u32BE).</summary>
    public const int FileCountOffset = 0x20;

    /// <summary>Absolute byte offset to the first file-table entry (u32BE).</summary>
    public const int FileTableOffset = 0x30;

    /// <summary>Byte size of the file table (u32BE), normally FileCount * FileTableEntrySize.</summary>
    public const int FileTableSizeOffset = 0x34;

    /// <summary>Absolute byte offset where GPU texture data begins (u32BE); normally 0x1000.</summary>
    public const int HeaderSizeOffset = 0x44;

    /// <summary>Total GPU data size across all textures (u32BE).</summary>
    public const int TotalGpuDataSizeOffset = 0x54;

    public const int FileTableEntrySize = 24;

    public const int TextureDescriptorSize = 0x38;

    /// <summary>Standard header size (GPU data start) used when rebuilding a file from scratch.</summary>
    public const int DefaultHeaderSize = 0x1000;

    // File-table entry field offsets, relative to the start of a 24-byte entry.
    public const int EntryOffsetField = 0x00;
    public const int EntrySizeField = 0x08;
    public const int EntryAlignmentField = 0x0C;
    public const int EntryTypeTagField = 0x14;
    public const int EntryAlignmentDataMarker = 0x1000;

    // File-table entry type tags (4 bytes, read as a big-endian uint32 for easy switch/compare).
    public const uint TypeTagTextureDataA = 0x00010031;
    public const uint TypeTagTextureDataB = 0x00000000;
    public const uint TypeTagDescriptor = 0x00020003;
    public const uint TypeTagDescriptorCombined = 0x000200E8;
    public const uint TypeTagNameTableA = 0x00EC0010;
    public const uint TypeTagNameTableB = 0x00EB000B;

    // Texture descriptor field offsets, relative to the descriptor block start (the
    // file-table entry's own offset field for a TypeTagDescriptor/TypeTagDescriptorCombined
    // entry). Cross-validated: the shipped Noesis plugin reads fields starting at
    // (offset field + 33) -- e.g. its format-code read at "+2" from that pointer is
    // offset+35 = offset+0x23, which is exactly DescriptorFormatCodeOffset below. Reading
    // fields directly at (descriptor base + these absolute offsets) is the same math
    // without the intermediate "+33" indirection.
    public const int DescriptorSizeFlagOffset = 0x1C;
    public const int DescriptorTileIndexOffset = 0x20;
    public const int DescriptorFormatCodeOffset = 0x23;
    public const int DescriptorHeightEncodedOffset = 0x25;
    public const int DescriptorWidthEncodedOffset = 0x26;
    public const int DescriptorNameTableOffsetOffset = 0x2C;
    public const int DescriptorScalingFactorOffset = 0x30;
    public const int DescriptorMipCountOffset = 0x37;

    // Name table layout.
    public const int NameTableCountAOffset = 0x00;
    public const int NameTableCountBOffset = 0x02;
    public const int NameTableEntriesStartOffset = 0x08;
    public const int NameTableEntrySize = 12;
    public const int NameEntryAddressOffset = 0x00;
    public const int NameEntryTypeTagOffset = 0x04;
    public const int NameEntryTextureIndexOffset = 0x08;
    public const uint NameTypeAbsolute = 0x00020003;
    public const uint NameTypeRelative = 0x00000000;
}
