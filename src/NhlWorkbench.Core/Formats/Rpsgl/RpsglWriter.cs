using System.Buffers.Binary;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rpsgl;

internal static class RpsglWriter
{
    /// <summary>
    /// PS3 texture data is stored linearly (no console-specific tiling, unlike RX2), so
    /// an in-place patch is just a direct byte copy into the original GPU data region.
    /// Only valid when every staged replacement matches its original's format/dimensions/
    /// mip count (enforced by <see cref="RpsglContainer.GetPatchCapabilities"/>).
    /// </summary>
    public static byte[] PatchInPlace(byte[] originalBytes, IReadOnlyList<TextureEntry> originals, IReadOnlyDictionary<int, TextureEntry> staged)
    {
        var result = (byte[])originalBytes.Clone();

        foreach ((int index, TextureEntry replacement) in staged)
        {
            TextureEntry original = originals[index];
            if (original.WriteBackMetadata is not RpsglWriteBackInfo info)
            {
                continue;
            }

            byte[] source = BufferUtilities.SafeSlice(replacement.LinearPixelData, 0, info.GpuDataSize);
            if (replacement.Format == TextureFormat.Argb8888)
            {
                source = RpsglReader.ReverseRawPixels(source);
            }

            int copyLength = Math.Min(source.Length, result.Length - info.GpuDataOffsetAbsolute);
            if (copyLength > 0)
            {
                Array.Copy(source, 0, result, info.GpuDataOffsetAbsolute, copyLength);
            }
        }

        return result;
    }

    /// <summary>
    /// Recomputes cumulative GPU offsets for every texture (using the replacement where
    /// staged, the original pixel data otherwise) starting from the standard
    /// <see cref="RpsglLayout.DefaultGpuStart"/> base, patches each changed descriptor's
    /// format/dimensions/mip-count fields and each record's cumulative-end field, and
    /// appends a freshly laid out GPU data blob. The header prefix (magic, fixed bytes,
    /// texlib records, descriptors) is preserved from the original file and only the
    /// fields that must change are patched -- consistent with the RX2 writer's approach.
    ///
    /// Note: if any original texture in this file was resolved via the special GPU-start
    /// override (see <see cref="RpsglWriteBackInfo.UsedGpuStartOverride"/>, used by a
    /// handful of special-purpose files like centerlogo/sock), Rebuild always switches
    /// that file to the standard cumulative-offset layout -- this is a documented Phase 1
    /// simplification for an already-rare, already-experimental code path.
    /// </summary>
    public static byte[] Rebuild(byte[] originalBytes, IReadOnlyList<TextureEntry> originals, IReadOnlyDictionary<int, TextureEntry> staged)
    {
        int headerLength = Math.Max(RpsglLayout.DefaultGpuStart, Math.Min(originalBytes.Length, RpsglLayout.DefaultGpuStart));
        var header = new byte[headerLength];
        Array.Copy(originalBytes, header, Math.Min(headerLength, originalBytes.Length));

        var perTextureLinearBlobs = new byte[originals.Count][];
        int cumulative = 0;

        for (int i = 0; i < originals.Count; i++)
        {
            TextureEntry original = originals[i];
            TextureEntry effective = staged.TryGetValue(i, out TextureEntry? replacement) ? replacement : original;

            if (original.WriteBackMetadata is not RpsglWriteBackInfo info)
            {
                perTextureLinearBlobs[i] = Array.Empty<byte>();
                continue;
            }

            ushort formatCode = RpsglFormatCodes.TryGetCode(effective.Format, out ushort code) ? code : info.OriginalFormatCode;

            int mipCount = Math.Max(1, effective.MipCount);
            int expectedSize = RpsglReader.ComputeReferenceMipChainSize(effective.Width, effective.Height, formatCode, mipCount);

            byte[] blob = BufferUtilities.SafeSlice(effective.LinearPixelData, 0, expectedSize);
            if (effective.Format == TextureFormat.Argb8888)
            {
                blob = RpsglReader.ReverseRawPixels(blob);
            }

            perTextureLinearBlobs[i] = blob;

            bool shapeChanged = effective.Width != original.Width || effective.Height != original.Height
                || effective.Format != original.Format || mipCount != original.MipCount;
            if (shapeChanged && info.DescriptorBase + RpsglLayout.DescriptorMipCountOffset + 1 <= header.Length)
            {
                RpsglTextureDescriptor.PatchFields(header, info.DescriptorBase, formatCode, effective.Width, effective.Height, (byte)mipCount);
            }

            cumulative += blob.Length;
            if (info.RecordOffset + RpsglLayout.RecordCumulEndOffset + 4 <= header.Length)
            {
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(info.RecordOffset + RpsglLayout.RecordCumulEndOffset, 4), cumulative);
            }
        }

        var result = new byte[headerLength + cumulative];
        Array.Copy(header, result, header.Length);

        int cursor = 0;
        for (int i = 0; i < perTextureLinearBlobs.Length; i++)
        {
            byte[] blob = perTextureLinearBlobs[i];
            Array.Copy(blob, 0, result, headerLength + cursor, blob.Length);
            cursor += blob.Length;
        }

        return result;
    }
}
