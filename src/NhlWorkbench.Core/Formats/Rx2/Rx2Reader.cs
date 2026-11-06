using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

public static class Rx2Reader
{
    public static bool IsRx2(byte[] data)
    {
        if (data.Length < Rx2HeaderLayout.MagicLength)
        {
            return false;
        }

        for (int i = 0; i < Rx2HeaderLayout.MagicLength; i++)
        {
            if (data[i] != Rx2HeaderLayout.Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    public static Result<Rx2Container> Parse(byte[] data, string sourcePath)
    {
        try
        {
            if (!IsRx2(data))
            {
                return Result.Failure<Rx2Container>("Not an RX2 container (magic mismatch).");
            }

            var warnings = new List<ParseWarning>();

            if (data.Length < Rx2HeaderLayout.HeaderSizeOffset + 4)
            {
                return Result.Failure<Rx2Container>("File is too small to contain an RX2 header.");
            }

            uint fileCount = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileCountOffset);
            uint fileTableOffset = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileTableOffset);
            uint headerSize = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.HeaderSizeOffset);

            if (fileTableOffset >= data.Length)
            {
                return Result.Failure<Rx2Container>("RX2 file table offset is out of range.");
            }

            var dataOffsets = new List<int>();
            var dataSizes = new List<int>();
            var dataEntryTableOffsets = new List<int>();
            var descriptorOffsets = new List<int>();
            var nameTableOffsets = new List<int>();

            int entryBase = (int)fileTableOffset;
            long maxEntries = Math.Min(fileCount, (data.Length - entryBase) / (long)Rx2HeaderLayout.FileTableEntrySize);

            for (int i = 0; i < maxEntries; i++)
            {
                int entryOffset = entryBase + (i * Rx2HeaderLayout.FileTableEntrySize);
                Rx2FileTableEntry entry = Rx2FileTableEntry.ReadAt(data, entryOffset);

                if ((entry.TypeTag == Rx2HeaderLayout.TypeTagTextureDataA || entry.TypeTag == Rx2HeaderLayout.TypeTagTextureDataB)
                    && entry.AlignmentField == Rx2HeaderLayout.EntryAlignmentDataMarker)
                {
                    dataOffsets.Add(entry.OffsetField);
                    dataSizes.Add(entry.SizeField);
                    dataEntryTableOffsets.Add(entryOffset);
                }
                else if (entry.TypeTag == Rx2HeaderLayout.TypeTagDescriptor)
                {
                    descriptorOffsets.Add(entry.OffsetField);
                }
                else if (entry.TypeTag == Rx2HeaderLayout.TypeTagDescriptorCombined && i >= 1)
                {
                    int prevEntryOffset = entryBase + ((i - 1) * Rx2HeaderLayout.FileTableEntrySize);
                    Rx2FileTableEntry prevEntry = Rx2FileTableEntry.ReadAt(data, prevEntryOffset);
                    dataOffsets.Add(prevEntry.OffsetField);
                    dataSizes.Add(prevEntry.SizeField);
                    dataEntryTableOffsets.Add(prevEntryOffset);
                    descriptorOffsets.Add(entry.OffsetField);
                }
                else if (entry.TypeTag == Rx2HeaderLayout.TypeTagNameTableA || entry.TypeTag == Rx2HeaderLayout.TypeTagNameTableB)
                {
                    nameTableOffsets.Add(entry.OffsetField);
                }
            }

            int textureCount = Math.Min(dataOffsets.Count, descriptorOffsets.Count);
            if (dataOffsets.Count != descriptorOffsets.Count)
            {
                warnings.Add(new ParseWarning(
                    $"Texture data block count ({dataOffsets.Count}) does not match descriptor count ({descriptorOffsets.Count}); using the smaller of the two.",
                    "Rx2Reader"));
            }

            string[]? names = nameTableOffsets.Count > 0
                ? Rx2NameTable.TryParse(data, nameTableOffsets[0], warnings)
                : null;

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var textures = new List<TextureEntry>(textureCount);

            for (int i = 0; i < textureCount; i++)
            {
                int descriptorBase = descriptorOffsets[i];
                if (descriptorBase < 0 || descriptorBase + Rx2HeaderLayout.TextureDescriptorSize > data.Length)
                {
                    warnings.Add(new ParseWarning($"Texture {i}: descriptor is out of range; skipping.", "Rx2Reader"));
                    continue;
                }

                Rx2TextureDescriptor descriptor = Rx2TextureDescriptor.ReadAt(data, descriptorBase);

                if (!Rx2FormatCodes.TryGetInfo(descriptor.FormatCode, out FormatElementInfo formatInfo))
                {
                    warnings.Add(new ParseWarning(
                        $"Texture {i}: unrecognized format code 0x{descriptor.FormatCode:X2}; skipping.", "Rx2Reader"));
                    continue;
                }

                int mipCount = Math.Max(1, (int)descriptor.MipCount);
                Rx2MipChainLayout layout = Rx2MipChainLayout.Compute(descriptor.Width, descriptor.Height, mipCount, formatInfo);

                int gpuOffsetAbsolute = dataOffsets[i] + (int)headerSize;
                byte[] tiledChain = BufferUtilities.SafeSlice(data, gpuOffsetAbsolute, Math.Max(dataSizes[i], layout.TotalTiledSize));

                byte[] linearChain = UntileMipChain(tiledChain, descriptor.Width, descriptor.Height, formatInfo, layout);

                string name = names is not null && i < names.Length ? names[i] : baseName;

                textures.Add(new TextureEntry
                {
                    Name = name,
                    Index = i,
                    Width = descriptor.Width,
                    Height = descriptor.Height,
                    Format = formatInfo.Format,
                    MipCount = mipCount,
                    LinearPixelData = linearChain,
                    WriteBackMetadata = new Rx2WriteBackInfo
                    {
                        GpuDataOffsetAbsolute = gpuOffsetAbsolute,
                        GpuDataSize = dataSizes[i],
                        DescriptorBase = descriptorBase,
                        DataEntryTableOffset = dataEntryTableOffsets[i],
                        OriginalFormatCode = descriptor.FormatCode,
                    },
                });
            }

            var container = new Rx2Container(data, sourcePath, textures);
            return Result.Success(container, warnings);
        }
        catch (Exception ex)
        {
            return Result.Failure<Rx2Container>($"Unhandled error parsing RX2 container: {ex.Message}", ex);
        }
    }

    private static byte[] UntileMipChain(byte[] tiledChain, int width, int height, FormatElementInfo formatInfo, Rx2MipChainLayout layout)
    {
        bool needsSwap = formatInfo.Layout == PixelLayout.CompressedBlock || formatInfo.BytesPerElement == 2;
        byte[] swapped = needsSwap ? X360Tiling.SwapEndian16(tiledChain) : tiledChain;

        var result = new byte[layout.TotalLinearSize];

        for (int m = 0; m < layout.TiledOffsets.Length; m++)
        {
            int mipWidth = Math.Max(1, width >> m);
            int mipHeight = Math.Max(1, height >> m);
            byte[] mipTiled = BufferUtilities.SafeSlice(swapped, layout.TiledOffsets[m], layout.TiledSizes[m]);

            byte[] mipLinear = formatInfo.Layout == PixelLayout.CompressedBlock
                ? X360Tiling.Untile(mipTiled, mipWidth, mipHeight, formatInfo.BytesPerElement)
                : X360Tiling.UntileRawPixelsExperimental(mipTiled, mipWidth, mipHeight, formatInfo.BytesPerElement);

            Array.Copy(mipLinear, 0, result, layout.LinearOffsets[m], Math.Min(mipLinear.Length, layout.LinearSizes[m]));
        }

        return result;
    }
}
