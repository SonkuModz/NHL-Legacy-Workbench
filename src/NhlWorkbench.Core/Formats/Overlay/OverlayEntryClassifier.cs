using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Bigf;

namespace NhlWorkbench.Core.Formats.Overlay;

/// <summary>
/// Classifies one BIGF entry from an overlay archive into the APT/texture sub-formats it
/// actually carries. Layers APT detection on top of the existing <see
/// cref="BigEntryClassifier"/> rather than reimplementing texture detection -- an entry this
/// classifier calls <see cref="OverlayEntryKind.Texture"/> is exactly what <see
/// cref="BigEntryClassifier"/> would call Rx2/Rpsgl/Dds/Xpr.
/// </summary>
public static class OverlayEntryClassifier
{
    private const int MaxRefPackPeekBytes = 4 * 1024 * 1024;

    public static OverlayEntryKind Classify(byte[] entryData) => Classify(entryData, out _);

    /// <summary>Same classification, but also hands back the RefPack-decompressed bytes when the entry turned out to be a compressed Movie or ConstantFile -- callers that need the parsed content should use this overload instead of decompressing a second time.</summary>
    public static OverlayEntryKind Classify(byte[] entryData, out byte[]? decompressed)
    {
        decompressed = null;

        if (entryData.Length == 0)
        {
            return OverlayEntryKind.Marker;
        }

        if (AptMovieLooksLikePrefix(entryData))
        {
            return OverlayEntryKind.Movie;
        }

        if (AptConstantFileReader.IsAptConstantFile(entryData))
        {
            return OverlayEntryKind.ConstantFile;
        }

        BigEntryFormat baseFormat = BigEntryClassifier.Classify(entryData);
        if (baseFormat is BigEntryFormat.Rx2 or BigEntryFormat.Rpsgl or BigEntryFormat.Dds or BigEntryFormat.Xpr)
        {
            return OverlayEntryKind.Texture;
        }

        if (RefPack.IsRefPack(entryData) && entryData.Length <= MaxRefPackPeekBytes)
        {
            Result<byte[]> peek = RefPack.Decompress(entryData);
            if (peek.IsSuccess)
            {
                byte[] inner = peek.Value!;
                if (AptMovieLooksLikePrefix(inner))
                {
                    decompressed = inner;
                    return OverlayEntryKind.Movie;
                }

                if (AptConstantFileReader.IsAptConstantFile(inner))
                {
                    decompressed = inner;
                    return OverlayEntryKind.ConstantFile;
                }
            }
        }

        if (AptShapeRecordReader.LooksLikeShapeRecord(entryData))
        {
            return OverlayEntryKind.ShapeRecord;
        }

        return OverlayEntryKind.Unknown;
    }

    private static bool AptMovieLooksLikePrefix(byte[] data) =>
        data.Length >= 9 && System.Text.Encoding.ASCII.GetString(data, 0, 9).Equals("Apt Data:", StringComparison.Ordinal);
}
