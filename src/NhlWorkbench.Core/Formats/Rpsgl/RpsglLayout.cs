namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>
/// Named byte offsets for the RPSGL (PlayStation 3) texlib-record container layout.
/// Cross-validated against the working Python prototype's <c>_TEXLIB_*</c> constants and
/// the shipped Noesis Python plugin's magic-byte check.
/// </summary>
public static class RpsglLayout
{
    // NOTE: cannot use a u8 string literal here -- 0x89 is outside ASCII, so a UTF-8
    // literal would encode it as the two bytes 0xC2 0x89 instead of the single raw byte
    // the file format actually uses.
    public static readonly byte[] Magic = { 0x89, (byte)'R', (byte)'W', (byte)'4', (byte)'p', (byte)'s', (byte)'3' };

    public const int MagicLength = 7;

    /// <summary>Absolute byte offset to the first texlib record.</summary>
    public const int TexlibRecordBase = 0x3A0;

    public const int TexlibRecordSize = 0x30;

    public static readonly byte[] TexlibRecordMagic = { 0x00, 0x01, 0x00, 0x30 };

    /// <summary>Absolute offset into the descriptor this record points to (u32BE), relative to the record start.</summary>
    public const int RecordDescriptorPointerOffset = 0x04;

    /// <summary>Cumulative end offset of this texture's GPU data (u32BE), relative to the record start.</summary>
    public const int RecordCumulEndOffset = 0x1C;

    /// <summary>The actual texture descriptor starts this many bytes after the record's descriptor pointer value.</summary>
    public const int DescriptorBaseAdjustment = 0x18;

    public const int DescriptorFormatOffset = 0x0A;
    public const int DescriptorWidthOffset = 0x18;
    public const int DescriptorHeightOffset = 0x1A;
    public const int DescriptorMipCountOffset = 0x1C;

    /// <summary>
    /// Optional GPU-start override (u32BE), present only in a handful of special-purpose
    /// files (e.g. centerlogo, sock) where the standard cumulative-offset math doesn't
    /// hold. Sits at the same header position RX2 uses for its own header-size field --
    /// a plausible shared convention across both console variants of this container format.
    /// </summary>
    public const int GpuStartOverrideFieldOffset = 0x44;

    /// <summary>Standard base offset for the first texture's GPU data when no override applies.</summary>
    public const int DefaultGpuStart = 0x0500;

    /// <summary>Safety cap on texlib record scanning so a malformed file can never hang the parser.</summary>
    public const int MaxRecordScan = 256;
}
