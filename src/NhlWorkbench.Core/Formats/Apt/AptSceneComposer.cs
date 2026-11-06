using NhlWorkbench.Core.Formats.Overlay;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Builds a paintable scene from a movie's real placement/transform layer -- the
/// second, decisive step (2026-08-19) after an earlier version of this class that
/// composed purely from each Shape/EditText's own authored local bounds and produced
/// a scene clustered near the origin (several real shapes sit at local (0,0), which
/// is correct for content authored relative to its own parent container, not the
/// stage). That version was honest about the gap; this one closes it: starting from
/// the root Movie character, it walks <see cref="AptFrameTableReader"/>'s frame-0
/// placements, resolves each placement's characterId via <see
/// cref="AptMovie.ResolveCharacterTagIndex"/>, and recurses into nested Sprite/Movie
/// placements -- composing each level's cumulative translate/scale down to real,
/// spread-across-the-1280x720-stage positions for every Shape and EditText leaf. See
/// <see cref="AptSceneElement"/> for exactly what X/Y/Width/Height mean now.
///
/// Known simplification, stated rather than hidden: only the placement matrix's
/// diagonal (scale) terms are applied, not the full 2x2 (skew/rotation terms B/C are
/// ignored). Every real placement sampled so far has a B/C so close to zero it reads
/// as denormal float noise, not an authored rotation, so this is a safe approximation
/// for the real data seen -- not a claim that rotation can never appear.
/// </summary>
public static class AptSceneComposer
{
    /// <summary>
    /// Caps how many levels of Sprite-within-Sprite are expanded. Deliberately shallow (1): the
    /// root Movie's own frame table is thoroughly validated real data, and expanding one level
    /// into a directly-placed Sprite's own frame table also produces sane, spread-across-the-stage
    /// positions -- but going a level deeper repeatedly walks into small groups of sprites that
    /// place each other (a genuine mutual-reference tangle in real data, not a bug in the cycle
    /// guard below, confirmed by tracing it directly), and without a shallow cap here the composite
    /// would either need the cycle guard to silently truncate real content or run away in size. A
    /// shallow, confidently-correct scene beats a deep, partly-fabricated-by-omission one.
    /// </summary>
    private const int MaxRecursionDepth = 1;

    /// <summary>
    /// Hard cap on total emitted elements. Recursion depth alone doesn't bound total work: a
    /// Sprite with, say, 50 placements nested a few levels inside other Sprites with similarly
    /// many placements branches combinatorially, not linearly -- observed in practice as an
    /// effectively-hung compose on real data before this cap was added. Real overlay scenes are a
    /// few hundred elements at most, so this is a safety net, not a realistic ceiling.
    /// </summary>
    private const int MaxElements = 6000;

    public static IReadOnlyList<AptSceneElement> Compose(OverlayArchive archive, AptMovie movie)
    {
        var elements = new List<AptSceneElement>();
        AptTagRecord? movieTag = movie.Tags.FirstOrDefault(t => t.Type == AptCharacterType.Movie);
        if (movieTag is null)
        {
            return elements;
        }

        var pathOffsets = new HashSet<int> { movieTag.Offset };
        ComposeCharacter(archive, movie, movieTag.Offset, originX: 0, originY: 0, originScaleX: 1, originScaleY: 1, originTint: 0xFFFFFFFF, depthBase: 0, recursionDepth: 0, pathOffsets, elements);

        return Deduplicate(elements).OrderBy(e => e.Depth).ToList();
    }

    /// <summary>
    /// Collapses near-identical placements down to one. Real data legitimately places the same
    /// shared shape/sprite template from several different parent placements (e.g. a "panel
    /// background" shape reused by several sibling team/period/clock panels), but a large share
    /// of the resulting duplicates land at essentially the SAME resolved screen position and
    /// size -- confirmed the direct cause of a solid-looking wash once vector placeholders were
    /// given any fill at all, however low the per-element opacity (2026-08-19). Keyed on kind +
    /// content identity + position/size rounded to the nearest pixel, so genuinely distinct
    /// instances (a ticker's letters at different X, or the same template used for a different
    /// real panel) are never merged -- only literal near-duplicates are.
    /// </summary>
    private static List<AptSceneElement> Deduplicate(List<AptSceneElement> elements)
    {
        var seen = new HashSet<(AptSceneElementKind Kind, int GeometryKey, string Text, int X, int Y, int Width, int Height)>();
        var result = new List<AptSceneElement>(elements.Count);
        foreach (AptSceneElement e in elements)
        {
            var key = (e.Kind, e.GeometryKey, e.Text, (int)MathF.Round(e.X), (int)MathF.Round(e.Y), (int)MathF.Round(e.Width), (int)MathF.Round(e.Height));
            if (seen.Add(key))
            {
                result.Add(e);
            }
        }

        return result;
    }

    private static void ComposeCharacter(
        OverlayArchive archive,
        AptMovie movie,
        int characterTagOffset,
        float originX,
        float originY,
        float originScaleX,
        float originScaleY,
        uint originTint,
        int depthBase,
        int recursionDepth,
        HashSet<int> pathOffsets,
        List<AptSceneElement> elements)
    {
        if (recursionDepth > MaxRecursionDepth || elements.Count >= MaxElements)
        {
            return;
        }

        foreach (AptPlaceObjectRecord placement in AptFrameTableReader.ReadFrameZeroPlacements(movie.RawData, characterTagOffset))
        {
            if (elements.Count >= MaxElements)
            {
                return;
            }

            int childIndex = movie.ResolveCharacterTagIndex(placement.CharacterId);
            if (childIndex < 0)
            {
                continue;
            }

            AptTagRecord childTag = movie.Tags[childIndex];
            float absX = originX + (placement.TranslateX * originScaleX);
            float absY = originY + (placement.TranslateY * originScaleY);
            float absScaleX = originScaleX * placement.MatrixA;
            float absScaleY = originScaleY * placement.MatrixD;
            int absDepth = depthBase + (int)placement.Depth;
            uint absTint = ComposeTint(originTint, placement.ApproximateColor);

            switch (childTag.Type)
            {
                case AptCharacterType.Shape:
                    if (AptShapeCharacter.TryParse(childTag) is { } shape)
                    {
                        EmitShape(archive, shape, absX, absY, absScaleX, absScaleY, absDepth, absTint, elements);
                    }

                    break;

                case AptCharacterType.EditText:
                    if (AptEditTextCharacter.TryParse(childTag, movie.RawData) is { } text)
                    {
                        EmitText(text, absX, absY, absScaleX, absScaleY, absDepth, absTint, elements);
                    }

                    break;

                case AptCharacterType.Sprite:
                case AptCharacterType.Movie:
                    // Cycle guard: real data has been observed where a Sprite's own placement list
                    // (in)directly places a character back onto its own recursion path -- without
                    // this, that compounds translate/scale every pass and the composite explodes
                    // to billions of units within a handful of levels. Refusing to re-enter a
                    // character already on the current path is the standard, correct fix (distinct
                    // from the depth/element caps above, which only bound the damage after the fact).
                    if (pathOffsets.Add(childTag.Offset))
                    {
                        ComposeCharacter(archive, movie, childTag.Offset, absX, absY, absScaleX, absScaleY, absTint, absDepth, recursionDepth + 1, pathOffsets, elements);
                        pathOffsets.Remove(childTag.Offset);
                    }

                    break;

                default:
                    // Font/Image/Button/Morph/Text characters were never observed placed directly
                    // in any real sample (Images are always reached indirectly, through a Shape's
                    // GeometryKey) -- skip rather than guess at semantics with no real example.
                    break;
            }
        }
    }

    /// <summary>
    /// Composes a leaf's own placement tint with everything inherited from its ancestor Sprite
    /// placements down to it. Found necessary 2026-08-19 after wiring color through for the first
    /// time and discovering every single leaf Shape's OWN placement carries the ubiquitous default
    /// (opaque white, no real tint -- 5082 of 5601 real placements sampled), while the actual
    /// distinctive colors (a real gold/yellow, several white-at-reduced-alpha fade states) live on
    /// SPRITE-level placements instead -- i.e. a whole panel is tinted once, at the point its
    /// container Sprite is placed, not re-stated on every one of its child shapes. Without this,
    /// every VectorRectangle rendered as the same flat default regardless of which real panel it
    /// belonged to. Standard per-channel multiply composition (harmless no-op when either side is
    /// the default: multiplying by 255/255 leaves the other side unchanged).
    /// </summary>
    private static uint ComposeTint(uint parent, uint own)
    {
        byte pa = (byte)(parent >> 24), pr = (byte)(parent >> 16), pg = (byte)(parent >> 8), pb = (byte)parent;
        byte oa = (byte)(own >> 24), or_ = (byte)(own >> 16), og = (byte)(own >> 8), ob = (byte)own;

        byte a = (byte)(pa * oa / 255);
        byte r = (byte)(pr * or_ / 255);
        byte g = (byte)(pg * og / 255);
        byte b = (byte)(pb * ob / 255);

        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static void EmitShape(OverlayArchive archive, AptShapeCharacter shape, float placementX, float placementY, float scaleX, float scaleY, int depth, uint tintColor, List<AptSceneElement> elements)
    {
        OverlayArchiveEntry? backing = archive.TryResolveShapeGeometryEntry(shape.GeometryKey);

        if (backing?.Kind == OverlayEntryKind.Texture)
        {
            (float localX, float localY, float width, float height) = Normalize(shape.X, shape.Y, shape.Width, shape.Height);
            float x = placementX + (localX * scaleX);
            float y = placementY + (localY * scaleY);
            elements.Add(new AptSceneElement(AptSceneElementKind.TexturedShape, x, y, width * MathF.Abs(scaleX), height * MathF.Abs(scaleY), shape.GeometryKey, backing.Name, 0, string.Empty, depth, tintColor));
            return;
        }

        // ShapeRecord (real vector geometry) or unresolved: try to decode the real rectangle from
        // the entry's own vertex data rather than trusting the character's declared box, which is
        // only a coarse bounding hint (confirmed 2026-08-19: the declared box can start at a
        // different origin than the shape's real geometry -- e.g. real height 45 decoded from
        // vertices at Y=[4..49] vs a declared box that implicitly assumes Y starts at 0).
        if (backing?.Kind == OverlayEntryKind.ShapeRecord)
        {
            byte[] entryData = archive.GetLogicalEntryData(backing.Name);
            if (AptShapeGeometry.TryDecodeRectangle(entryData) is { } rect)
            {
                (float rx, float ry, float rw, float rh) = Normalize(rect.X, rect.Y, rect.Width, rect.Height);
                float x2 = placementX + (rx * scaleX);
                float y2 = placementY + (ry * scaleY);
                elements.Add(new AptSceneElement(AptSceneElementKind.VectorRectangle, x2, y2, rw * MathF.Abs(scaleX), rh * MathF.Abs(scaleY), shape.GeometryKey, backing.Name, 0, string.Empty, depth, tintColor));
                return;
            }
        }

        (float localX2, float localY2, float width2, float height2) = Normalize(shape.X, shape.Y, shape.Width, shape.Height);
        float fx = placementX + (localX2 * scaleX);
        float fy = placementY + (localY2 * scaleY);
        elements.Add(new AptSceneElement(AptSceneElementKind.VectorShape, fx, fy, width2 * MathF.Abs(scaleX), height2 * MathF.Abs(scaleY), shape.GeometryKey, backing?.Name, 0, string.Empty, depth, tintColor));
    }

    private static void EmitText(AptEditTextCharacter text, float placementX, float placementY, float scaleX, float scaleY, int depth, uint tintColor, List<AptSceneElement> elements)
    {
        (float localX, float localY, float width, float height) = Normalize(text.Left, text.Top, text.Width, text.Height);
        float x = placementX + (localX * scaleX);
        float y = placementY + (localY * scaleY);

        elements.Add(new AptSceneElement(AptSceneElementKind.Text, x, y, width * MathF.Abs(scaleX), height * MathF.Abs(scaleY), -1, null, text.Color, text.Text, depth, tintColor));
    }

    /// <summary>A negative width/height means the authored corner was the opposite one -- shift the origin so the box is always positive-size, top-left-origin.</summary>
    private static (float X, float Y, float Width, float Height) Normalize(float x, float y, float width, float height)
    {
        float left = width < 0 ? x + width : x;
        float top = height < 0 ? y + height : y;
        return (left, top, MathF.Abs(width), MathF.Abs(height));
    }
}
