using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Walks a movie's tag stream -- the region after the class/import table's string
/// pool that <see cref="AptMovieReader"/> previously left entirely unparsed. See
/// <see cref="AptTagRecord"/> for what's confirmed about the universal record shape.
///
/// SUPERSEDES an earlier offset-table-based walker (2026-08-19): that approach
/// trusted a table of big-endian u32 offsets immediately preceding the tag stream,
/// stopping as soon as a candidate offset's own +4 didn't carry the magic sentinel.
/// It was quietly wrong -- cross-checked against a from-scratch sequential walk that
/// instead trusts each character type's own FIXED record length (Movie=68, Shape=36,
/// EditText=68, Font=28, Sprite=28, Image=20 bytes -- confirmed by majority-vote
/// across every tag the offset-table approach DID find) and simply steps by that
/// length record-to-record with no offset table at all. The sequential walk finds
/// far more real records in every sample: 160 vs 59 (overlay_0), 65 vs 48
/// (overlay_39), 21 vs 20 (overlay_53) -- the offset-table approach was undercounting
/// by roughly two thirds. The walk still stops the moment it meets a type outside
/// this known set or a length that would overrun the buffer; what follows at that
/// point is a *different* structure (the frame/placement table -- see <see
/// cref="AptFrameTableReader"/>), not more character records, so stopping there is
/// correct rather than a shortfall.
/// </summary>
public static class AptTagStreamReader
{
    /// <summary>Fixed byte length per character type, confirmed by majority-vote across every real record found in 3 independent NHL overlay movies.</summary>
    public static readonly IReadOnlyDictionary<AptCharacterType, int> KnownLengths = new Dictionary<AptCharacterType, int>
    {
        [AptCharacterType.Movie] = 68,
        [AptCharacterType.Shape] = 36,
        [AptCharacterType.EditText] = 68,
        [AptCharacterType.Font] = 28,
        [AptCharacterType.Sprite] = 28,
        [AptCharacterType.Image] = 20,
    };

    public static IReadOnlyList<AptTagRecord> ReadTags(byte[] movieData, int searchStart)
    {
        var tags = new List<AptTagRecord>();

        // searchStart is where the (now-superseded) offset table begins, not the first tag's own
        // byte position -- its very first u32 entry is still a real, valid pointer to that first
        // tag (confirmed: this is exactly candidate #1 the old table-walking reader used to find),
        // so read just that one entry to locate the true start, then walk sequentially from there.
        int tableStart = AlignUp(searchStart, 4);
        if (tableStart + 4 > movieData.Length)
        {
            return tags;
        }

        int pos = BigEndianReader.ReadInt32BEAt(movieData, tableStart);
        if (pos < tableStart || pos + 16 > movieData.Length || BigEndianReader.ReadUInt32BEAt(movieData, pos + 4) != AptTagRecord.Magic)
        {
            return tags;
        }

        while (pos + 16 <= movieData.Length)
        {
            uint magic = BigEndianReader.ReadUInt32BEAt(movieData, pos + 4);
            if (magic != AptTagRecord.Magic)
            {
                break;
            }

            uint typeValue = BigEndianReader.ReadUInt32BEAt(movieData, pos);
            if (!KnownLengths.TryGetValue((AptCharacterType)typeValue, out int length) || pos + length > movieData.Length)
            {
                break;
            }

            uint fieldA = BigEndianReader.ReadUInt32BEAt(movieData, pos + 8);
            uint fieldB = BigEndianReader.ReadUInt32BEAt(movieData, pos + 12);
            byte[] raw = BufferUtilities.SafeSlice(movieData, pos, length);

            tags.Add(new AptTagRecord(pos, length, (AptCharacterType)typeValue, fieldA, fieldB, raw));
            pos += length;
        }

        return tags;
    }

    private static int AlignUp(int value, int alignment) => ((value + alignment - 1) / alignment) * alignment;
}
