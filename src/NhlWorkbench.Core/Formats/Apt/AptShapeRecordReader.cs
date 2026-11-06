using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

public static class AptShapeRecordReader
{
    /// <summary>True if this entry's first 4 bytes (big-endian) equal its own length -- the confirmed self-length invariant that identifies a shape record, distinct from a texture, movie, or constant file.</summary>
    public static bool LooksLikeShapeRecord(byte[] data) =>
        data.Length >= 8 && BigEndianReader.ReadUInt32BEAt(data, 0) == (uint)data.Length;

    public static Result<AptShapeRecord> Parse(byte[] data)
    {
        try
        {
            if (data.Length < 8)
            {
                return Result.Failure<AptShapeRecord>("Entry is too small to be a shape record (needs at least 8 bytes).");
            }

            int declaredTotalSize = BigEndianReader.ReadInt32BEAt(data, 0);
            int childCount = BigEndianReader.ReadInt32BEAt(data, 4);
            return Result.Success(new AptShapeRecord(declaredTotalSize, childCount, data));
        }
        catch (Exception ex)
        {
            return Result.Failure<AptShapeRecord>($"Unhandled error parsing Apt shape record: {ex.Message}", ex);
        }
    }
}
