using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>The texture descriptor a texlib record points to (base = record's descriptor pointer + 0x18).</summary>
public readonly struct RpsglTextureDescriptor
{
    public ushort FormatCode { get; }

    public int Width { get; }

    public int Height { get; }

    public byte MipCount { get; }

    private RpsglTextureDescriptor(ushort formatCode, int width, int height, byte mipCount)
    {
        FormatCode = formatCode;
        Width = width;
        Height = height;
        MipCount = mipCount;
    }

    public static RpsglTextureDescriptor ReadAt(byte[] data, int descriptorBase)
    {
        ushort formatCode = BigEndianReader.ReadUInt16BEAt(data, descriptorBase + RpsglLayout.DescriptorFormatOffset);
        ushort width = BigEndianReader.ReadUInt16BEAt(data, descriptorBase + RpsglLayout.DescriptorWidthOffset);
        ushort height = BigEndianReader.ReadUInt16BEAt(data, descriptorBase + RpsglLayout.DescriptorHeightOffset);
        byte mipCount = data[descriptorBase + RpsglLayout.DescriptorMipCountOffset];

        return new RpsglTextureDescriptor(formatCode, width, height, mipCount);
    }

    /// <summary>Patches the format/dimensions/mip-count fields in place -- used by <see cref="RpsglWriter"/>'s Rebuild path when a replacement's shape differs from the original.</summary>
    public static void PatchFields(byte[] header, int descriptorBase, ushort formatCode, int width, int height, byte mipCount)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(descriptorBase + RpsglLayout.DescriptorFormatOffset, 2), formatCode);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(descriptorBase + RpsglLayout.DescriptorWidthOffset, 2), (ushort)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(descriptorBase + RpsglLayout.DescriptorHeightOffset, 2), (ushort)height);
        header[descriptorBase + RpsglLayout.DescriptorMipCountOffset] = mipCount;
    }
}
