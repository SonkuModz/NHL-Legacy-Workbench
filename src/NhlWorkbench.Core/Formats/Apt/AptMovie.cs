namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// A parsed "Apt Data" movie -- the RefPack-compressed entry (conventionally named "0") inside
/// an overlay .big archive that EA's APT/Scaleform-derived UI system uses to describe one
/// component. See <see cref="AptMovieReader"/> for exactly what is and is not decoded.
///
/// Two layers of confirmed structure, both cross-validated against 3 independent real movies
/// (overlay_0 "StaticAdWrapper", overlay_39 "NHLDPad", overlay_53 "LogoPill") rather than a
/// single lucky sample:
///
/// 1. The class/import header (version string + this movie's own class identity + every
///    imported component class), via the same string-pool-offset arithmetic used since this
///    format was first parsed (see <see cref="AptMovieReader"/>'s remarks).
/// 2. The tag stream (<see cref="Tags"/>) -- newly decoded 2026-08-19, after a fellow modder
///    shared community research on the same EA APT format from NCAA Football 14
///    (References/aptresearch/), which named the character-type numbering (Shape=1,
///    EditText=2, Font=3, Sprite=5, Image=7, Movie=9) this format's own tag `Type` field turned
///    out to match exactly. See <see cref="AptTagRecord"/>, <see cref="AptShapeCharacter"/>,
///    <see cref="AptEditTextCharacter"/>, <see cref="AptFontCharacter"/>, and <see
///    cref="AptMovieCharacter"/> for what's confirmed about each type's own fields -- Sprite
///    (5) and Image (7) records are walked and exposed as raw <see cref="AptTagRecord"/>s only;
///    their type-specific field layouts are not yet decoded to the same standard.
/// </summary>
public sealed class AptMovie
{
    /// <summary>The literal version string, e.g. "Apt Data:1:7:4" -- every real sample seen so far uses "1:7:4".</summary>
    public required string VersionString { get; init; }

    /// <summary>The single byte immediately after the version string (before the class table). Identical (0x1A) across every real sample checked; meaning unconfirmed, so it is surfaced raw rather than interpreted.</summary>
    public required byte HeaderTagByte { get; init; }

    /// <summary>Index 0 is this movie's own class identity; every entry after it is an imported component class this movie depends on.</summary>
    public required IReadOnlyList<AptClassReference> ClassReferences { get; init; }

    /// <summary>Every null-terminated ASCII string (4+ chars) found anywhere in the movie, with its byte offset -- includes symbol/instance names (e.g. "mcBg"), additional class paths, and font names. A flat scan of the whole file, not scoped to a confirmed "string pool region", so it is a safe superset rather than a claim about structure.</summary>
    public required IReadOnlyList<(int Offset, string Value)> AllStrings { get; init; }

    /// <summary>Every tag-stream record found, in file order -- see this class's remarks. Empty for a movie whose tag stream couldn't be located (e.g. corrupt/truncated input); a real movie always has at least the one Movie-type header tag.</summary>
    public required IReadOnlyList<AptTagRecord> Tags { get; init; }

    /// <summary>The full decompressed movie bytes, for the developer hex panel.</summary>
    public required byte[] RawData { get; init; }

    public string ClassName => ClassReferences.Count > 0 ? ClassReferences[0].ShortName : string.Empty;

    public string ClassPath => ClassReferences.Count > 0 ? ClassReferences[0].FullPath : string.Empty;

    public IEnumerable<AptClassReference> Imports => ClassReferences.Skip(1);

    /// <summary>The Movie-type tag (always exactly one, always first) -- carries the real stage width/height. Null only if the tag stream couldn't be located at all.</summary>
    public AptMovieCharacter? Stage => Tags
        .Where(t => t.Type == AptCharacterType.Movie)
        .Select(AptMovieCharacter.TryParse)
        .FirstOrDefault(m => m is not null);

    public IReadOnlyList<AptShapeCharacter> Shapes => Tags
        .Where(t => t.Type == AptCharacterType.Shape)
        .Select(AptShapeCharacter.TryParse)
        .Where(s => s is not null)
        .Select(s => s!)
        .ToList();

    public IReadOnlyList<AptEditTextCharacter> EditTexts => Tags
        .Where(t => t.Type == AptCharacterType.EditText)
        .Select(t => AptEditTextCharacter.TryParse(t, RawData))
        .Where(e => e is not null)
        .Select(e => e!)
        .ToList();

    public IReadOnlyList<AptFontCharacter> Fonts => Tags
        .Where(t => t.Type == AptCharacterType.Font)
        .Select(t => AptFontCharacter.TryParse(t, RawData))
        .Where(f => f is not null)
        .Select(f => f!)
        .ToList();

    /// <summary>
    /// Resolves a PlaceObject record's raw <c>characterId</c> to an index into <see cref="Tags"/>.
    /// Confirmed 2026-08-19 against real placements in every sample movie: character IDs are
    /// assigned by walking the tag stream in order (id 0 = <see cref="Tags"/>[0], id 1 = Tags[1],
    /// ...), EXCEPT that every entry in <see cref="ClassReferences"/> (this movie's own class at
    /// index 0, plus every import) reserves one id slot at its own <see
    /// cref="AptClassReference.Aux1"/> value, shifting every local tag's effective id up by one
    /// for each reserved slot at or below it. Verified by cross-checking several real characterIds
    /// this way and finding every one resolves to a character type that makes sense to place
    /// directly (Shape, Sprite, EditText) -- e.g. overlay_39's characterId 67 resolves past its 3
    /// reserved slots (48, 61, 66) to local tag index 64, its own last-defined Sprite.
    /// </summary>
    public int ResolveCharacterTagIndex(uint characterId)
    {
        int reservedAtOrBelow = ClassReferences.Count(cr => cr.Aux1 <= characterId);
        int index = (int)characterId - reservedAtOrBelow;
        return index >= 0 && index < Tags.Count ? index : -1;
    }
}
