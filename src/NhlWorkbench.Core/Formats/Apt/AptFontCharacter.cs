namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// The <see cref="AptCharacterType.Font"/> tag -- confirmed 2026-08-19: FieldA-adjacent
/// offset at base+16 is a byte offset into the movie's own string data, resolving to
/// the real font name in every real sample checked (e.g. "EA HelveticaNeue Con Bold"),
/// verified by exact offset match against the independently-scanned string table.
/// </summary>
public sealed record AptFontCharacter(AptTagRecord Tag, string FontName)
{
    public static AptFontCharacter? TryParse(AptTagRecord tag, byte[] movieData)
    {
        if (tag.Type != AptCharacterType.Font || tag.RawData.Length < 20)
        {
            return null;
        }

        int nameOffset = Utilities.BigEndianReader.ReadInt32BEAt(tag.RawData, 16);
        string name = nameOffset >= 0 && nameOffset < movieData.Length
            ? AptStringScanner.ReadPrintableRun(movieData, nameOffset)
            : string.Empty;
        return new AptFontCharacter(tag, name);
    }
}
