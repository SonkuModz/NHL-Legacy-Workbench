using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// The 0x38-byte per-texture descriptor block. Width/height are packed encodings, not
/// raw integers -- see the field-offset comments in <see cref="Rx2HeaderLayout"/> for
/// how these were cross-validated against both reference sources.
/// </summary>
public readonly struct Rx2TextureDescriptor
{
    public byte SizeFlag { get; }

    public byte TileIndex { get; }

    public byte FormatCode { get; }

    public int Width { get; }

    public int Height { get; }

    public int NameTableOffset { get; }

    public ushort ScalingFactor { get; }

    public byte MipCount { get; }

    private Rx2TextureDescriptor(byte sizeFlag, byte tileIndex, byte formatCode, int width, int height, int nameTableOffset, ushort scalingFactor, byte mipCount)
    {
        SizeFlag = sizeFlag;
        TileIndex = tileIndex;
        FormatCode = formatCode;
        Width = width;
        Height = height;
        NameTableOffset = nameTableOffset;
        ScalingFactor = scalingFactor;
        MipCount = mipCount;
    }

    public static Rx2TextureDescriptor ReadAt(byte[] data, int descriptorBase)
    {
        byte sizeFlag = data[descriptorBase + Rx2HeaderLayout.DescriptorSizeFlagOffset];
        byte tileIndex = data[descriptorBase + Rx2HeaderLayout.DescriptorTileIndexOffset];
        byte formatCode = data[descriptorBase + Rx2HeaderLayout.DescriptorFormatCodeOffset];
        byte heightEncoded = data[descriptorBase + Rx2HeaderLayout.DescriptorHeightEncodedOffset];
        ushort widthEncoded = BigEndianReader.ReadUInt16BEAt(data, descriptorBase + Rx2HeaderLayout.DescriptorWidthEncodedOffset);
        int nameTableOffset = BigEndianReader.ReadInt32BEAt(data, descriptorBase + Rx2HeaderLayout.DescriptorNameTableOffsetOffset);
        ushort scalingFactor = BigEndianReader.ReadUInt16BEAt(data, descriptorBase + Rx2HeaderLayout.DescriptorScalingFactorOffset);
        byte mipCount = data[descriptorBase + Rx2HeaderLayout.DescriptorMipCountOffset];

        int height = (heightEncoded + 1) * 8;
        int width = (widthEncoded & 0x1FFF) + 1;

        return new Rx2TextureDescriptor(sizeFlag, tileIndex, formatCode, width, height, nameTableOffset, scalingFactor, mipCount);
    }

    /// <summary>
    /// Patches the dimension/format/mip-count-dependent descriptor fields in place
    /// (size flag, format code, height/width encoding, scaling factor, mip count).
    /// Everything else in the 0x38-byte block (fixed template bytes, tile index,
    /// name-table-offset) is left untouched -- used by <see cref="Rx2Writer"/>'s Rebuild
    /// path when a replacement texture's dimensions/format/mips differ from the original.
    /// </summary>
    public static void PatchDimensionFields(byte[] header, int descriptorBase, byte formatCode, int width, int height, byte mipCount, int bytesPerElement)
    {
        byte sizeFlag = (byte)(Math.Max(width, height) <= 512 ? 0x84 : 0x88);
        byte heightEncoded = (byte)((height / 8) - 1);
        ushort widthEncoded = (ushort)(((width - 1) & 0x1FFF) | 0xE000);
        ushort scalingFactor = ComputeScalingFactor(Math.Max(width, height), bytesPerElement);

        header[descriptorBase + Rx2HeaderLayout.DescriptorSizeFlagOffset] = sizeFlag;
        header[descriptorBase + Rx2HeaderLayout.DescriptorFormatCodeOffset] = formatCode;
        header[descriptorBase + Rx2HeaderLayout.DescriptorHeightEncodedOffset] = heightEncoded;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(descriptorBase + Rx2HeaderLayout.DescriptorWidthEncodedOffset, 2), widthEncoded);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(descriptorBase + Rx2HeaderLayout.DescriptorScalingFactorOffset, 2), scalingFactor);
        header[descriptorBase + Rx2HeaderLayout.DescriptorMipCountOffset] = mipCount;
    }

    private static ushort ComputeScalingFactor(int maxDimension, int bytesPerElement)
    {
        int divided = maxDimension / 512;
        int factor = divided * divided;
        if (factor == 0)
        {
            factor = 1;
        }

        int scaling = (bytesPerElement / 4) * factor;
        return (ushort)Math.Max(1, scaling);
    }
}
