namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// The <see cref="AptCharacterType.Movie"/> tag -- always exactly one per movie, and
/// always the first tag-stream record. Confirmed 2026-08-19 against 3 real NHL
/// overlay archives: <see cref="StageWidth"/>/<see cref="StageHeight"/> at fixed
/// offsets +36/+40 read 1280/720 in every sample (a standard HD render-target size,
/// not a coincidental pair of small integers), giving the Scorebug Editor's preview
/// canvas a real, non-guessed coordinate space for the first time.
/// </summary>
public sealed record AptMovieCharacter(AptTagRecord Tag, int StageWidth, int StageHeight)
{
    public static AptMovieCharacter? TryParse(AptTagRecord tag)
    {
        if (tag.Type != AptCharacterType.Movie || tag.RawData.Length < 44)
        {
            return null;
        }

        int width = Utilities.BigEndianReader.ReadInt32BEAt(tag.RawData, 36);
        int height = Utilities.BigEndianReader.ReadInt32BEAt(tag.RawData, 40);
        return new AptMovieCharacter(tag, width, height);
    }
}
