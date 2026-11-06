using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>
/// Ported line-for-line from the reference prototype's <c>_parse_textures</c> (the
/// function actually used for real .rpsgl files, verified directly against the game's
/// own reference tool implementation rather than reconstructed from a description) --
/// earlier drafts of this reader were based on a secondhand summary of the format and
/// were missing several behaviors confirmed here: the texlib-record-base scanning
/// fallback, the cumul_end &lt;= prev_cumul guard, and the final min(tex_size, block_size,
/// remaining) clamp on how many bytes are actually read.
/// </summary>
public static class RpsglReader
{
    /// <summary>Texlib record scanning searches this range (matching the reference's <c>range(0x100, min(len, 0x2000), 4)</c>) if the fixed base doesn't have the record magic.</summary>
    private const int RecordScanStart = 0x100;
    private const int RecordScanEnd = 0x2000;

    public static bool IsRpsgl(byte[] data)
    {
        if (data.Length < RpsglLayout.MagicLength)
        {
            return false;
        }

        for (int i = 0; i < RpsglLayout.MagicLength; i++)
        {
            if (data[i] != RpsglLayout.Magic[i])
            {
                return false;
            }
        }

        return true;
    }

    public static Result<RpsglContainer> Parse(byte[] data, string sourcePath)
    {
        try
        {
            if (!IsRpsgl(data))
            {
                return Result.Failure<RpsglContainer>("Not an RPSGL container (magic mismatch).");
            }

            var warnings = new List<ParseWarning>();

            // Every RPSGL file matches the 7-byte magic, which itself starts with the
            // 4-byte "\x89RW4" prefix, so the GPU-start override field is always read --
            // whether it's actually *used* is decided per-texture by the "bogus cumulative
            // size" heuristic below, matching the reference exactly.
            int gpuStartOverride = data.Length >= RpsglLayout.GpuStartOverrideFieldOffset + 4
                ? BigEndianReader.ReadInt32BEAt(data, RpsglLayout.GpuStartOverrideFieldOffset)
                : 0;

            int? recordBase = FindRecordBase(data);
            if (recordBase is null)
            {
                warnings.Add(new ParseWarning(
                    "No texlib record table found (neither at the fixed offset nor via a scan). This RPSGL file may use a different sub-format that Phase 1 does not parse.",
                    "RpsglReader"));
                return Result.Success(new RpsglContainer(data, sourcePath, new List<TextureEntry>()), warnings);
            }

            int recordCount = CountConsecutiveRecords(data, recordBase.Value);

            // Confirmed by decoding a real pant RPSGL both ways and byte-comparing the result
            // against a verified-correct reference: EVERY one of the 78 real pant texlib RPSGL
            // files has a "large" (non-bogus-looking) recordBlock that fools the heuristic below
            // into picking RpsglLayout.DefaultGpuStart, but the header's gpuStartOverride is
            // actually the correct offset for all of them regardless -- unlike jersey/sock/
            // goaliehelmet files (and at least two unrelated generic texlib files spot-checked in
            // the test corpus), which correctly resolve to the override via the heuristic's own
            // "bogus block size" signal and must NOT be forced, since their correctness there is
            // unverified. Keying off the "pant" path segment (rather than loosening the numeric
            // heuristic for everyone) keeps this fix scoped to the one file category actually
            // proven to need it.
            bool isPantSourceFile = sourcePath
                .Split('\\', '/')
                .Any(segment => string.Equals(segment, "pant", StringComparison.OrdinalIgnoreCase));

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var textures = new List<TextureEntry>();
            int prevCumul = 0;

            for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                int recordOffset = recordBase.Value + (recordIndex * RpsglLayout.TexlibRecordSize);
                RpsglTexlibRecord record = RpsglTexlibRecord.ReadAt(data, recordOffset);
                int descriptorBase = record.DescriptorPointer + RpsglLayout.DescriptorBaseAdjustment;

                if (descriptorBase < 0 || descriptorBase + 0x20 > data.Length)
                {
                    prevCumul = record.CumulEnd;
                    continue;
                }

                RpsglTextureDescriptor descriptor = RpsglTextureDescriptor.ReadAt(data, descriptorBase);

                if (descriptor.Width == 0 || descriptor.Height == 0
                    || !RpsglFormatCodes.TryGetInfo(descriptor.FormatCode, out FormatElementInfo formatInfo))
                {
                    prevCumul = record.CumulEnd;
                    continue;
                }

                int mipCount = Math.Max(1, (int)descriptor.MipCount);
                int expectedSize = ComputeReferenceMipChainSize(descriptor.Width, descriptor.Height, descriptor.FormatCode, mipCount);

                int recordBlock = record.CumulEnd > prevCumul ? record.CumulEnd - prevCumul : expectedSize;

                int gpuStart;
                int blockSize;
                bool usedOverride = isPantSourceFile || recordBlock < expectedSize / 4;
                if (usedOverride)
                {
                    gpuStart = gpuStartOverride + prevCumul;
                    blockSize = expectedSize;
                }
                else
                {
                    gpuStart = RpsglLayout.DefaultGpuStart + prevCumul;
                    blockSize = recordBlock;
                }

                int actual = Math.Min(Math.Min(expectedSize, blockSize), Math.Max(0, data.Length - gpuStart));
                if (actual == 0)
                {
                    prevCumul = record.CumulEnd;
                    continue;
                }

                byte[] linear = BufferUtilities.SafeSlice(data, gpuStart, actual);

                if (descriptor.FormatCode == RawPixelFormatCode)
                {
                    linear = ReverseRawPixels(linear);
                }

                if (isPantSourceFile && formatInfo.BytesPerElement == 8)
                {
                    linear = CorrectPantMipTailGapIfNeeded(data, gpuStart, descriptor.Width, descriptor.Height, mipCount, formatInfo, linear);
                }

                textures.Add(new TextureEntry
                {
                    Name = textures.Count == 0 ? baseName : $"{baseName}_{textures.Count}",
                    Index = textures.Count,
                    Width = descriptor.Width,
                    Height = descriptor.Height,
                    Format = formatInfo.Format,
                    MipCount = mipCount,
                    LinearPixelData = linear,
                    WriteBackMetadata = new RpsglWriteBackInfo
                    {
                        GpuDataOffsetAbsolute = gpuStart,
                        GpuDataSize = actual,
                        RecordOffset = recordOffset,
                        DescriptorBase = descriptorBase,
                        OriginalFormatCode = descriptor.FormatCode,
                        UsedGpuStartOverride = usedOverride,
                    },
                });

                prevCumul = record.CumulEnd;
            }

            if (textures.Count == 0)
            {
                warnings.Add(new ParseWarning(
                    "The texlib record table was found but no valid textures were extracted from it.", "RpsglReader"));
            }

            var container = new RpsglContainer(data, sourcePath, textures);
            return Result.Success(container, warnings);
        }
        catch (Exception ex)
        {
            return Result.Failure<RpsglContainer>($"Unhandled error parsing RPSGL container: {ex.Message}", ex);
        }
    }

    internal const ushort RawPixelFormatCode = 0x6007;

    private static int? FindRecordBase(byte[] data)
    {
        if (data.Length >= RpsglLayout.TexlibRecordBase + 4
            && RpsglTexlibRecord.MagicMatches(data, RpsglLayout.TexlibRecordBase))
        {
            return RpsglLayout.TexlibRecordBase;
        }

        int scanEnd = Math.Min(data.Length, RecordScanEnd);
        for (int scan = RecordScanStart; scan + 4 <= scanEnd; scan += 4)
        {
            if (RpsglTexlibRecord.MagicMatches(data, scan))
            {
                return scan;
            }
        }

        return null;
    }

    private static int CountConsecutiveRecords(byte[] data, int recordBase)
    {
        int count = 0;
        int pos = recordBase;
        while (pos + RpsglLayout.TexlibRecordSize <= data.Length && RpsglTexlibRecord.MagicMatches(data, pos))
        {
            count++;
            pos += RpsglLayout.TexlibRecordSize;
        }

        return count;
    }

    /// <summary>
    /// Some real pant .rpsgl files (confirmed: 14 of 78 in the test corpus) insert an extra
    /// 384-byte gap in their GPU data between the last mip whose block dimensions are still
    /// above 8x8 and the small "mip tail" that follows (verified by brute-force offset search
    /// against a user-provided known-good reference file, then cross-checked against the full
    /// corpus by requiring the fix to strictly *improve* mip-to-mip color continuity, never
    /// regress it). The other 64 pant files are tightly packed with no gap at all. Nothing in
    /// either file's header (record CumulEnd, mip count, dimensions) distinguishes the two
    /// groups -- both this project's own reader and the byte layout of the two groups are
    /// otherwise identical -- so the only reliable signal found is the decoded content itself:
    /// a real diffuse color texture's average color barely changes between adjacent mip
    /// levels (mipmapping is a low-pass filter), so a big jump right at the big-mip/tail
    /// boundary is a strong, specific tell that the tail is being read from the wrong offset.
    /// Scoped to pant sources with 8-byte-per-block formats (DXT1) only -- the only case
    /// this was validated against; anything else takes the untouched tight-packed reading.
    /// </summary>
    private static readonly DdsCodec MipTailCorrectionCodec = new();

    private static byte[] CorrectPantMipTailGapIfNeeded(byte[] data, int gpuStart, int width, int height, int mipCount, FormatElementInfo formatInfo, byte[] tightLinear)
    {
        const int CandidateGap = 384;
        const double MinimumImprovementToSwitch = 5.0;
        int bpb = formatInfo.BytesPerElement;

        var tightOffsets = new int[mipCount];
        var tightSizes = new int[mipCount];
        int cursor = 0;
        int tailStartMip = -1;

        for (int m = 0; m < mipCount; m++)
        {
            int mipWidth = Math.Max(1, width >> m);
            int mipHeight = Math.Max(1, height >> m);
            int blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            int blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
            int size = blocksWide * blocksHigh * bpb;

            tightOffsets[m] = cursor;
            tightSizes[m] = size;
            cursor += size;

            if (tailStartMip < 0 && blocksWide <= 8 && blocksHigh <= 8)
            {
                tailStartMip = m;
            }
        }

        // Need at least one "big" mip to compare the tail against, and an actual tail to correct.
        if (tailStartMip <= 0 || tailStartMip >= mipCount)
        {
            return tightLinear;
        }

        int lastBigMip = tailStartMip - 1;
        int lastBigMipOffset = tightOffsets[lastBigMip];
        int lastBigMipSize = tightSizes[lastBigMip];
        if (lastBigMipOffset + lastBigMipSize > tightLinear.Length)
        {
            return tightLinear;
        }

        int tailOffset = tightOffsets[tailStartMip];
        int tailSize = tightLinear.Length - tailOffset;
        if (tailSize <= 0)
        {
            return tightLinear;
        }

        int gappedSourceOffset = gpuStart + tailOffset + CandidateGap;
        if (gappedSourceOffset < 0 || gappedSourceOffset + tailSize > data.Length)
        {
            return tightLinear;
        }

        int lastBigMipWidth = Math.Max(1, width >> lastBigMip);
        int lastBigMipHeight = Math.Max(1, height >> lastBigMip);
        int tailMipWidth = Math.Max(1, width >> tailStartMip);
        int tailMipHeight = Math.Max(1, height >> tailStartMip);

        (double R, double G, double B)? bigAverage = DecodeAverageColor(tightLinear, lastBigMipOffset, lastBigMipSize, lastBigMipWidth, lastBigMipHeight, formatInfo.Format);
        (double R, double G, double B)? tightTailAverage = DecodeAverageColor(tightLinear, tailOffset, tightSizes[tailStartMip], tailMipWidth, tailMipHeight, formatInfo.Format);
        byte[] gappedTail = BufferUtilities.SafeSlice(data, gappedSourceOffset, tailSize);
        (double R, double G, double B)? gappedTailAverage = DecodeAverageColor(gappedTail, 0, tightSizes[tailStartMip], tailMipWidth, tailMipHeight, formatInfo.Format);

        if (bigAverage is null || tightTailAverage is null || gappedTailAverage is null)
        {
            return tightLinear;
        }

        double tightDistance = ColorDistance(bigAverage.Value, tightTailAverage.Value);
        double gappedDistance = ColorDistance(bigAverage.Value, gappedTailAverage.Value);

        if (tightDistance - gappedDistance < MinimumImprovementToSwitch)
        {
            return tightLinear;
        }

        byte[] corrected = (byte[])tightLinear.Clone();
        Array.Copy(gappedTail, 0, corrected, tailOffset, tailSize);
        return corrected;
    }

    private static (double R, double G, double B)? DecodeAverageColor(byte[] buffer, int offset, int size, int mipWidth, int mipHeight, TextureFormat format)
    {
        if (offset < 0 || offset + size > buffer.Length)
        {
            return null;
        }

        byte[] slice = buffer.AsSpan(offset, size).ToArray();
        Result<Rgba32Image> decoded = MipTailCorrectionCodec.Decode(slice, mipWidth, mipHeight, format);
        if (!decoded.IsSuccess)
        {
            return null;
        }

        byte[] pixels = decoded.Value!.Pixels;
        long r = 0, g = 0, b = 0;
        int pixelCount = pixels.Length / 4;
        if (pixelCount == 0)
        {
            return null;
        }

        for (int p = 0; p < pixels.Length; p += 4)
        {
            r += pixels[p];
            g += pixels[p + 1];
            b += pixels[p + 2];
        }

        return (r / (double)pixelCount, g / (double)pixelCount, b / (double)pixelCount);
    }

    private static double ColorDistance((double R, double G, double B) a, (double R, double G, double B) b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    /// <summary>
    /// Matches the reference's <c>_mip_chain_size</c> exactly: block-based sizing
    /// (ceil(w/4) * ceil(h/4) * bpb) for every format, with bpb = 8 only for DXT1
    /// (0x83F1) and 16 for everything else -- including the raw 0x6007 format, which is
    /// not truly block-shaped. This is only used here as an expected-size bound for the
    /// bogus-cumulative-offset heuristic and the final byte-count clamp, so replicating
    /// the reference's formula (rather than a more "correct" per-format one) is what
    /// keeps this reader's byte-range decisions compatible with real files.
    /// </summary>
    /// <summary>
    /// Raw 0x6007 pixels are stored as big-endian A,R,G,B, while <see cref="TextureFormat.Argb8888"/> means
    /// DXGI B8G8R8A8, whose memory order is B,G,R,A. Reversing each pixel converts either way, so the reader
    /// and writer share this and everything between them sees one order.
    /// </summary>
    internal static byte[] ReverseRawPixels(byte[] pixels)
    {
        var result = (byte[])pixels.Clone();
        for (int i = 0; i + 4 <= result.Length; i += 4)
        {
            (result[i], result[i + 1], result[i + 2], result[i + 3]) = (pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
        }

        return result;
    }

    internal static int ComputeReferenceMipChainSize(int width, int height, ushort formatCode, int mipCount)
    {
        int bpb = formatCode == 0x83F1 ? 8 : 16;
        int total = 0;
        for (int m = 0; m < Math.Max(1, mipCount); m++)
        {
            int mipWidth = Math.Max(1, width >> m);
            int mipHeight = Math.Max(1, height >> m);
            if (formatCode == RawPixelFormatCode)
            {
                total += mipWidth * mipHeight * 4;
                continue;
            }

            int blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            int blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
            total += blocksWide * blocksHigh * bpb;
        }

        return total;
    }
}
