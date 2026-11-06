using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Formats.Xpr;

namespace NhlWorkbench.Core.Formats.Bigf;

/// <summary>
/// Identifies what a single BIGF entry's bytes actually are, so the Texture Editor (and
/// Batch Tools) can tell texture-bearing entries apart from everything else a real .big
/// archive carries (UI bytecode, lookup tables, section-label markers, etc.) without
/// duplicating the same "try RX2, then RPSGL" probing logic in more than one place.
/// </summary>
public static class BigEntryClassifier
{
    private static readonly byte[] DdsMagic = { (byte)'D', (byte)'D', (byte)'S', (byte)' ' };

    /// <summary>RefPack entries this size or smaller are decompressed during classification to
    /// identify their inner format; larger ones are left Unknown rather than paying an
    /// unbounded decompression cost just to label a row in the archive browser.</summary>
    private const int MaxRefPackPeekBytes = 4 * 1024 * 1024;

    public static BigEntryFormat Classify(byte[] entryData)
    {
        if (entryData.Length == 0)
        {
            return BigEntryFormat.Marker;
        }

        if (Rx2Reader.IsRx2(entryData))
        {
            return BigEntryFormat.Rx2;
        }

        if (RpsglReader.IsRpsgl(entryData))
        {
            return BigEntryFormat.Rpsgl;
        }

        if (IsDds(entryData))
        {
            return BigEntryFormat.Dds;
        }

        if (XprContainer.IsXpr(entryData))
        {
            return BigEntryFormat.Xpr;
        }

        if (RefPack.IsRefPack(entryData) && entryData.Length <= MaxRefPackPeekBytes)
        {
            Result<byte[]> decompressed = RefPack.Decompress(entryData);
            if (decompressed.IsSuccess && XprContainer.IsXpr(decompressed.Value!))
            {
                return BigEntryFormat.Xpr;
            }
        }

        return BigEntryFormat.Unknown;
    }

    private static bool IsDds(byte[] data) =>
        data.Length >= DdsMagic.Length
        && data[0] == DdsMagic[0] && data[1] == DdsMagic[1] && data[2] == DdsMagic[2] && data[3] == DdsMagic[3];
}
