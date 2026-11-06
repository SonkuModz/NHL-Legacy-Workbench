using NhlWorkbench.Core.Formats.Overlay;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Builds a "components in place" preview list -- the real root-level elements (per the
/// depth-replay true final display list, <see cref="AptPlacementEditor.ComputeFinalDisplayListByOffset"/>),
/// each with a position, a size where one is knowable, and (for texture-backed Shapes) the real
/// bitmap to show.
///
/// Deliberately does NOT attempt to run ActionScript: the real on-screen position of most panels
/// is set by <c>gAnimator.Animate(...)</c> calls at runtime (confirmed 2026-08-21 via a real
/// bytecode disassembly of this file's `PositionPanels`/`RepositionPanels` functions), which this
/// static preview cannot execute -- it shows where things START (the file's own baked
/// translateX/Y), not necessarily their final animated resting position. This is the same
/// fundamental limit documented throughout this session's investigation, not a new gap.
///
/// Also deliberately does NOT recurse into a Sprite/Movie's own children: this session found real,
/// unresolved reliability problems enumerating nested children for many sprites (they appear to
/// use an older frame-table field layout this workbench's readers don't correctly detect,
/// producing wrong/overlapping results for containers like the team/text sprites) -- rather than
/// present unreliable nested content as if it were confirmed, every Sprite/Movie is rendered as a
/// labeled marker only. Direct root-level Shapes (real textures, confirmed via the
/// <c>GeometryKey+2</c> archive-entry binding) and EditText fields are the reliable parts of this
/// preview.
///
/// Each element also carries <see cref="AptPositionedElement.RespondsToMove"/> (via <see
/// cref="AptPlacementEditor.IsKnownStaticResponsive"/>) so the preview can show, per element,
/// whether the Move/anchor tools in this editor will actually affect it -- confirmed
/// script-controlled elements (team-color panels, every score/clock/period text field) and the
/// one empirically-frozen element (the "mcAd" advertisement slot) never move via a static edit no
/// matter what; everything else (chrome, background, mask, glow, glints, team logos/the EA badge)
/// does. See `AptPlacementEditor`'s remarks for the full evidence behind this split.
/// </summary>
public static class AptPositionedPreviewBuilder
{
    public static IReadOnlyList<AptPositionedElement> Build(byte[] movieData, AptMovie movie, OverlayArchive archive)
    {
        AptTagRecord? movieTag = movie.Tags.FirstOrDefault(t => t.Type == AptCharacterType.Movie);
        if (movieTag is null)
        {
            return Array.Empty<AptPositionedElement>();
        }

        Dictionary<int, AptPlaceObjectRecord> final = AptPlacementEditor.ComputeFinalDisplayListByOffset(movieData, movieTag.Offset);
        var result = new List<AptPositionedElement>();

        foreach ((int offset, AptPlaceObjectRecord rec) in final)
        {
            int idx = movie.ResolveCharacterTagIndex(rec.CharacterId);
            if (idx < 0)
            {
                continue;
            }

            AptTagRecord tag = movie.Tags[idx];
            string resolvedName = AptPlacementEditor.ResolveInstanceName(movieData, movie, offset);
            bool respondsToMove = AptPlacementEditor.IsKnownStaticResponsive(resolvedName);
            string name = string.IsNullOrEmpty(resolvedName) ? $"cid{rec.CharacterId}" : resolvedName;

            switch (tag.Type)
            {
                case AptCharacterType.Shape:
                {
                    AptShapeCharacter? shape = AptShapeCharacter.TryParse(tag);
                    if (shape is null)
                    {
                        continue;
                    }

                    OverlayArchiveEntry? backing = archive.TryResolveShapeGeometryEntry(shape.GeometryKey);
                    if (backing?.Kind == OverlayEntryKind.Texture)
                    {
                        result.Add(new AptPositionedElement(name, rec.CharacterId, rec.TranslateX, rec.TranslateY, shape.Width, shape.Height, AptPositionedElementKind.TexturedShape, backing.Name, respondsToMove));
                    }
                    else
                    {
                        result.Add(new AptPositionedElement(name, rec.CharacterId, rec.TranslateX, rec.TranslateY, shape.Width, shape.Height, AptPositionedElementKind.VectorShape, null, respondsToMove));
                    }

                    break;
                }

                case AptCharacterType.EditText:
                {
                    AptEditTextCharacter? text = AptEditTextCharacter.TryParse(tag, movieData);
                    float w = text?.Width ?? 60f;
                    float h = text?.Height ?? 20f;
                    result.Add(new AptPositionedElement(name, rec.CharacterId, rec.TranslateX, rec.TranslateY, w, h, AptPositionedElementKind.Text, null, respondsToMove));
                    break;
                }

                case AptCharacterType.Sprite:
                case AptCharacterType.Movie:
                    result.Add(new AptPositionedElement(name, rec.CharacterId, rec.TranslateX, rec.TranslateY, 0, 0, AptPositionedElementKind.Container, null, respondsToMove));
                    break;
            }
        }

        return result;
    }
}
