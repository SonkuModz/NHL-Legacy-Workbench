using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Parses a decompressed "Apt Data" movie's class/import table. Ground-truthed by hand against
/// three independent real overlay movies (see <see cref="AptMovie"/>'s remarks); the parsing
/// algorithm below is the one piece of arithmetic that was actually derivable rather than
/// guessed, and it is what makes this robust across movies with different numbers of imports:
///
/// Layout: "Apt Data:&lt;major&gt;:&lt;minor&gt;:&lt;build&gt;" (printable ASCII, length varies
/// with version digit count) + 1 tag byte (0x1A in every sample) + padding to the next 4-byte
/// boundary, then N groups of 4 big-endian u32 fields. Field 0 of EVERY group is a byte offset
/// into a string pool that begins immediately after the last group -- which means field 0 of
/// GROUP 1 always equals (tableStart + N*16), because group 1's string (the movie's own class
/// path) is always the first string in that pool. That gives N directly:
/// <c>N = (group1Field0 - tableStart) / 16</c> -- confirmed exactly against overlay_0 (N=2),
/// overlay_39 (N=3), and overlay_53 (N=1), so the reader never has to guess or hard-code a
/// group count. Field 1 of each group is that class's short symbol name (same string-pool
/// convention). Fields 2/3 are read and preserved but NOT interpreted (see <see cref="AptClassReference"/>).
/// </summary>
public static class AptMovieReader
{
    private const string VersionPrefix = "Apt Data:";
    private const int GroupSize = 16; // 4 x u32BE

    public static Result<AptMovie> Parse(byte[] decompressedMovieData)
    {
        try
        {
            byte[] data = decompressedMovieData;
            if (data.Length < VersionPrefix.Length || !System.Text.Encoding.ASCII.GetString(data, 0, VersionPrefix.Length).Equals(VersionPrefix, StringComparison.Ordinal))
            {
                return Result.Failure<AptMovie>("Not an Apt Data movie (missing 'Apt Data:' prefix).");
            }

            string versionString = AptStringScanner.ReadPrintableRun(data, 0);
            int tagByteOffset = versionString.Length;
            if (tagByteOffset >= data.Length)
            {
                return Result.Failure<AptMovie>("Apt movie is truncated after the version string.");
            }

            byte tagByte = data[tagByteOffset];
            int tableStart = AlignUp(tagByteOffset + 1, 4);

            if (tableStart + GroupSize > data.Length)
            {
                return Result.Failure<AptMovie>("Apt movie is truncated before its class table.");
            }

            uint group1Field0 = BigEndianReader.ReadUInt32BEAt(data, tableStart);
            if (group1Field0 <= tableStart || (group1Field0 - tableStart) % GroupSize != 0)
            {
                return Result.Failure<AptMovie>($"Apt movie's class table has an inconsistent first offset (0x{group1Field0:X}) for a table starting at 0x{tableStart:X}.");
            }

            int groupCount = (int)((group1Field0 - tableStart) / GroupSize);
            if (groupCount <= 0 || groupCount > 256 || tableStart + (groupCount * GroupSize) > data.Length)
            {
                return Result.Failure<AptMovie>($"Apt movie's derived class-table count ({groupCount}) is out of range.");
            }

            var references = new List<AptClassReference>(groupCount);
            int classTableStringPoolEnd = 0;
            for (int i = 0; i < groupCount; i++)
            {
                int groupOffset = tableStart + (i * GroupSize);
                uint pathOffset = BigEndianReader.ReadUInt32BEAt(data, groupOffset);
                uint nameOffset = BigEndianReader.ReadUInt32BEAt(data, groupOffset + 4);
                uint aux1 = BigEndianReader.ReadUInt32BEAt(data, groupOffset + 8);
                uint aux2 = BigEndianReader.ReadUInt32BEAt(data, groupOffset + 12);

                string fullPath = pathOffset < data.Length ? AptStringScanner.ReadPrintableRun(data, (int)pathOffset) : string.Empty;
                string shortName = nameOffset < data.Length ? AptStringScanner.ReadPrintableRun(data, (int)nameOffset) : string.Empty;
                references.Add(new AptClassReference(fullPath, shortName, aux1, aux2));

                // The tag-stream offset table that follows starts right after the FURTHEST
                // string any group's fields actually reach -- not simply "after 2*groupCount
                // strings walked in sequence", because some offsets point INTO another
                // string's suffix rather than owning independent storage (confirmed on real
                // files: overlay_0/overlay_39 both do this). Re-deriving the end from each
                // resolved string's own offset+length is what makes this correct for every
                // real sample instead of only the single-group (no-suffix-sharing) case.
                classTableStringPoolEnd = Math.Max(classTableStringPoolEnd, EndOfStringAt(data, (int)pathOffset, fullPath));
                classTableStringPoolEnd = Math.Max(classTableStringPoolEnd, EndOfStringAt(data, (int)nameOffset, shortName));
            }

            List<(int Offset, string Value)> allStrings = AptStringScanner.ScanAll(data, 4);
            IReadOnlyList<AptTagRecord> tags = AptTagStreamReader.ReadTags(data, classTableStringPoolEnd);

            return Result.Success(new AptMovie
            {
                VersionString = versionString,
                HeaderTagByte = tagByte,
                ClassReferences = references,
                AllStrings = allStrings,
                Tags = tags,
                RawData = data,
            });
        }
        catch (Exception ex)
        {
            return Result.Failure<AptMovie>($"Unhandled error parsing Apt movie: {ex.Message}", ex);
        }
    }

    private static int EndOfStringAt(byte[] data, int offset, string resolved) =>
        offset >= 0 && offset < data.Length ? offset + resolved.Length + 1 : 0;

    private static int AlignUp(int value, int alignment) => ((value + alignment - 1) / alignment) * alignment;
}
