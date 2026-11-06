using System.Buffers.Binary;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

internal static class Rx2Writer
{
    /// <summary>
    /// Replaces only the GPU pixel bytes for staged textures, leaving the header,
    /// descriptors, and name table byte-for-byte unchanged. Only valid when every staged
    /// replacement matches its original's format/dimensions/mip count (enforced by
    /// <see cref="Rx2Container.GetPatchCapabilities"/> before this is called).
    /// </summary>
    public static byte[] PatchInPlace(byte[] originalBytes, IReadOnlyList<TextureEntry> originals, IReadOnlyDictionary<int, TextureEntry> staged)
    {
        var result = (byte[])originalBytes.Clone();

        foreach ((int index, TextureEntry replacement) in staged)
        {
            TextureEntry original = originals[index];
            if (original.WriteBackMetadata is not Rx2WriteBackInfo info)
            {
                continue;
            }

            if (!Rx2FormatCodes.TryGetInfo(info.OriginalFormatCode, out FormatElementInfo formatInfo))
            {
                continue;
            }

            Rx2MipChainLayout layout = Rx2MipChainLayout.Compute(replacement.Width, replacement.Height, replacement.MipCount, formatInfo);
            byte[] tiledBlob = BuildTiledBlob(replacement, formatInfo, layout);
            Array.Copy(tiledBlob, 0, result, info.GpuDataOffsetAbsolute, tiledBlob.Length);
        }

        return result;
    }

    /// <summary>
    /// Used when at least one staged replacement's dimensions/format/mip-count differ
    /// from its original (<see cref="PatchInPlace"/> handles the same-shape case).
    /// Non-destructive by construction: starts from a full clone of the original file, so
    /// every byte this method doesn't explicitly touch is preserved exactly as-is. This
    /// matters because some RX2 containers -- stadium/arena files in particular -- share
    /// their GPU-data region with non-texture data (embedded mesh geometry) that this
    /// reader/writer has no knowledge of; an earlier version of this method discarded that
    /// shared region entirely by repacking only the known textures from scratch, which
    /// silently corrupted the embedded meshes (confirmed: vertex counts stayed the same
    /// but positions decoded to garbage after a rebuild, because the mesh's untouched
    /// file-table offsets pointed at bytes that were no longer mesh data). A texture whose
    /// new tiled size fits in its original slot is overwritten in place at the same
    /// offset; one that grew is appended past the current end of the file instead, so no
    /// other texture or foreign data ever moves.
    /// </summary>
    public static byte[] Rebuild(byte[] originalBytes, IReadOnlyList<TextureEntry> originals, IReadOnlyDictionary<int, TextureEntry> staged)
    {
        byte[] buffer = (byte[])originalBytes.Clone();
        uint headerSize = BigEndianReader.ReadUInt32BEAt(buffer, Rx2HeaderLayout.HeaderSizeOffset);
        int originalLength = buffer.Length;

        foreach ((int index, TextureEntry replacement) in staged)
        {
            TextureEntry original = originals[index];
            if (original.WriteBackMetadata is not Rx2WriteBackInfo info)
            {
                continue;
            }

            bool dimensionsChanged = replacement.Width != original.Width || replacement.Height != original.Height
                || replacement.Format != original.Format || replacement.MipCount != original.MipCount;

            if (!dimensionsChanged)
            {
                // Same shape as the original -- an in-place pixel swap is all that's needed.
                if (Rx2FormatCodes.TryGetInfo(info.OriginalFormatCode, out FormatElementInfo sameFormatInfo))
                {
                    Rx2MipChainLayout sameLayout = Rx2MipChainLayout.Compute(replacement.Width, replacement.Height, replacement.MipCount, sameFormatInfo);
                    byte[] sameBlob = BuildTiledBlob(replacement, sameFormatInfo, sameLayout);
                    Array.Copy(sameBlob, 0, buffer, info.GpuDataOffsetAbsolute, sameBlob.Length);
                }

                continue;
            }

            byte formatCode = Rx2FormatCodes.TryGetCode(replacement.Format, out byte code) ? code : info.OriginalFormatCode;
            Rx2FormatCodes.TryGetInfo(formatCode, out FormatElementInfo formatInfo);
            int mipCount = Math.Max(1, replacement.MipCount);
            Rx2MipChainLayout layout = Rx2MipChainLayout.Compute(replacement.Width, replacement.Height, mipCount, formatInfo);
            byte[] tiledBlob = BuildTiledBlob(replacement, formatInfo, layout);

            int newOffsetAbsolute;
            if (tiledBlob.Length <= info.GpuDataSize)
            {
                // Fits in the texture's existing slot -- overwrite in place, offset unchanged.
                newOffsetAbsolute = info.GpuDataOffsetAbsolute;
                Array.Copy(tiledBlob, 0, buffer, newOffsetAbsolute, tiledBlob.Length);
            }
            else
            {
                // Doesn't fit -- append past the current end of the file so nothing already
                // written (other textures, embedded mesh data) ever has to move.
                newOffsetAbsolute = buffer.Length;
                Array.Resize(ref buffer, buffer.Length + tiledBlob.Length);
                Array.Copy(tiledBlob, 0, buffer, newOffsetAbsolute, tiledBlob.Length);
            }

            int newOffsetRelative = newOffsetAbsolute - (int)headerSize;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.DataEntryTableOffset + Rx2HeaderLayout.EntryOffsetField, 4), newOffsetRelative);
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.DataEntryTableOffset + Rx2HeaderLayout.EntrySizeField, 4), tiledBlob.Length);

            Rx2TextureDescriptor.PatchDimensionFields(buffer, info.DescriptorBase, formatCode, replacement.Width, replacement.Height, (byte)mipCount, formatInfo.BytesPerElement);
        }

        if (buffer.Length > originalLength)
        {
            uint newTotalGpuSize = (uint)(buffer.Length - headerSize);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(Rx2HeaderLayout.TotalGpuDataSizeOffset, 4), newTotalGpuSize);
        }

        return buffer;
    }

    /// <summary>Tiles a texture entry's full mip chain into a freshly-allocated, tightly-packed buffer.</summary>
    private static byte[] BuildTiledBlob(TextureEntry entry, FormatElementInfo formatInfo, Rx2MipChainLayout layout)
    {
        bool needsSwap = formatInfo.Layout == PixelLayout.CompressedBlock || formatInfo.BytesPerElement == 2;
        var tiledBlob = new byte[layout.TotalTiledSize];

        for (int m = 0; m < layout.TiledOffsets.Length; m++)
        {
            int mipWidth = Math.Max(1, entry.Width >> m);
            int mipHeight = Math.Max(1, entry.Height >> m);

            byte[] mipLinear = BufferUtilities.SafeSlice(entry.LinearPixelData, layout.LinearOffsets[m], layout.LinearSizes[m]);
            byte[] mipLinearSwapped = needsSwap ? X360Tiling.SwapEndian16(mipLinear) : mipLinear;

            if (formatInfo.Layout == PixelLayout.CompressedBlock)
            {
                X360Tiling.TileInto(tiledBlob, layout.TiledOffsets[m], mipLinearSwapped, mipWidth, mipHeight, formatInfo.BytesPerElement);
            }
            else
            {
                X360Tiling.TileRawPixelsIntoExperimental(tiledBlob, layout.TiledOffsets[m], mipLinearSwapped, mipWidth, mipHeight, formatInfo.BytesPerElement);
            }
        }

        return tiledBlob;
    }
}
