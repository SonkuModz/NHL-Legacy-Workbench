namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// The <see cref="AptCharacterType.Shape"/> tag -- a piece of vector geometry (fill,
/// mask, or background chrome). Field layout: <see cref="GeometryKey"/> confirmed at
/// FieldA (base+8); <see cref="Width"/>/<see cref="Height"/> confirmed at base+24/+28
/// by independent cross-reference against real mesh dimensions in the external
/// community research this format was cross-checked against (References/aptresearch/
/// APT_GEOMETRY.md: "Console Shape records with signature 0x09876543 use word 2
/// (base+8) as the big-endian geometry key. Width and height are big-endian floats at
/// base+24/+28"). <see cref="X"/>/<see cref="Y"/> at base+16/+20 are the natural
/// reading that pairs with that confirmed width/height as a position+size rectangle,
/// but unlike GeometryKey/Width/Height they have no independent cross-check of their
/// own yet -- treat them as likely-correct, not confirmed to the same standard.
///
/// <see cref="GeometryKey"/> -&gt; BIG-archive-entry binding is a real, verified NHL
/// finding (2026-08-19), not carried over from the external reference: across all 3
/// real overlay archives sampled, every single Shape's GeometryKey + 2 equals the
/// name of a real numbered archive entry (33/33 matches, 0 misses) -- see <see
/// cref="Overlay.OverlayArchive.TryResolveShapeGeometryEntry"/>.
/// </summary>
public sealed record AptShapeCharacter(AptTagRecord Tag, int GeometryKey, float X, float Y, float Width, float Height)
{
    public static AptShapeCharacter? TryParse(AptTagRecord tag)
    {
        if (tag.Type != AptCharacterType.Shape || tag.RawData.Length < 32)
        {
            return null;
        }

        int geometryKey = (int)tag.FieldA;
        float x = Utilities.BigEndianReader.ReadSingleBEAt(tag.RawData, 16);
        float y = Utilities.BigEndianReader.ReadSingleBEAt(tag.RawData, 20);
        float width = Utilities.BigEndianReader.ReadSingleBEAt(tag.RawData, 24);
        float height = Utilities.BigEndianReader.ReadSingleBEAt(tag.RawData, 28);
        return new AptShapeCharacter(tag, geometryKey, x, y, width, height);
    }
}
