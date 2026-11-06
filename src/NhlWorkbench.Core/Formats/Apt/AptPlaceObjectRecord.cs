using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One "PlaceObject" frame-item record -- the transform layer that was missing from
/// the initial character-table decode (2026-08-19 follow-up). Confirmed against real
/// NHL overlay bytes by cross-referencing the external NCAA14 community research's
/// own documented PlaceObject/PlaceObject3 layout (References/aptresearch/APT_DISCOVERIES.md,
/// "PlaceObject3" entry, 2026-06-30): a 64-byte record,
/// <c>[tag:u32][flags:u32][depth:u32][characterId:u32][matrixA,B,C,D:4xf32][translateX,Y:2xf32][colorAdd:u32][colorMul:u32][ratio:u32][namePtr:u32][clipDepth:u32]</c>.
/// <see cref="Tag"/> is 3 for a plain PlaceObject or 9 for PlaceObject3 (an 80-byte
/// variant with 4 trailing u32s this reader does not need); both share this same
/// 64-byte head. <see cref="CharacterId"/> of 0xFFFFFFFF (-1) is the documented "Move"
/// form -- re-transforms whatever is already placed at <see cref="Depth"/> rather than
/// placing something new; real NHL movies use this heavily for ticker-style animation
/// (a scrolling row of glyphs, one Move per frame stepping X). <see
/// cref="AptFrameTableReader"/> skips Move records: they carry no new character to
/// place, and skipping them is safe for a single-frame static composite (nothing is
/// lost, only repeated/animated positions of something already placed).
/// </summary>
public sealed record AptPlaceObjectRecord(
    uint Tag,
    uint Flags,
    uint Depth,
    uint CharacterId,
    float MatrixA,
    float MatrixB,
    float MatrixC,
    float MatrixD,
    float TranslateX,
    float TranslateY,
    uint ColorAdd,
    uint ColorMul)
{
    public const uint MoveCharacterId = 0xFFFFFFFF;

    public const int ByteLength = 64;

    /// <summary>
    /// Standard SWF PlaceObject2 flag bits, confirmed present in real NHL flags values (2026-08-19):
    /// e.g. flags=0x26 decodes to exactly HasCharacter|HasMatrix|HasName, matching the external
    /// research's own hand-annotated example record verbatim. <see cref="HasClipDepth"/> is the
    /// important one for rendering: an object placed with this bit set is a MASK (its own depth
    /// through <c>clipDepth</c> defines a clip region for other objects), not visible content in
    /// its own right -- confirmed as the real cause of a giant, wrongly-visible near-full-stage
    /// rectangle in the preview (the mask shape's own authored bounds are deliberately huge,
    /// since masks are sized to cover what they clip, not to look like normal UI chrome).
    /// </summary>
    public const uint FlagMove = 0x01;
    public const uint FlagHasCharacter = 0x02;
    public const uint FlagHasMatrix = 0x04;
    public const uint FlagHasColorTransform = 0x08;
    public const uint FlagHasRatio = 0x10;
    public const uint FlagHasName = 0x20;
    public const uint FlagHasClipDepth = 0x40;

    public bool IsClipMask => (Flags & FlagHasClipDepth) != 0;

    /// <summary>
    /// A best-effort approximate render color derived from <see cref="ColorAdd"/>/<see
    /// cref="ColorMul"/>, for placements that have no other color source (no material/fill-color
    /// field exists anywhere in the geometry records this workbench has decoded -- see <see
    /// cref="AptShapeGeometry"/>'s remarks). NOT independently confirmed against a known real
    /// in-game color -- this applies the standard SWF colorTransform formula
    /// (<c>final = original*mul/256 + add</c>) under the necessary assumption that the unknown
    /// "original" source color is opaque white, since real NHL data shows exactly two patterns:
    /// <c>colorMul</c>'s RGB is always 0 (so <c>colorAdd</c> alone IS the final color -- observed
    /// as white at varying alpha, e.g. dimmed/fade states) or <c>colorAdd</c>'s RGB is always 0
    /// with alpha forced opaque (so <c>colorMul</c>'s RGB alone is the final color -- observed
    /// values include a genuine, distinctive gold/yellow, 0xFEF300-ish). Both branches are
    /// consistent with every one of the 12 distinct real (colorAdd, colorMul) pairs found in
    /// overlay_0 -- but "consistent with observed data" is not the same as "proven," since no
    /// independently-known real color exists yet to cross-check against.
    /// </summary>
    public uint ApproximateColor
    {
        get
        {
            byte addA = (byte)(ColorAdd >> 24), addR = (byte)(ColorAdd >> 16), addG = (byte)(ColorAdd >> 8), addB = (byte)ColorAdd;
            if (addR != 0 || addG != 0 || addB != 0)
            {
                return ColorAdd; // white (or another add-only tint) at the add term's own alpha
            }

            byte mulR = (byte)(ColorMul >> 16), mulG = (byte)(ColorMul >> 8), mulB = (byte)ColorMul;
            return ((uint)addA << 24) | ((uint)mulR << 16) | ((uint)mulG << 8) | mulB;
        }
    }

    public static AptPlaceObjectRecord? TryParse(byte[] movieData, int offset)
    {
        if (offset < 0 || offset + ByteLength > movieData.Length)
        {
            return null;
        }

        uint tag = BigEndianReader.ReadUInt32BEAt(movieData, offset);
        if (tag != 3 && tag != 9)
        {
            return null;
        }

        return new AptPlaceObjectRecord(
            Tag: tag,
            Flags: BigEndianReader.ReadUInt32BEAt(movieData, offset + 4),
            Depth: BigEndianReader.ReadUInt32BEAt(movieData, offset + 8),
            CharacterId: BigEndianReader.ReadUInt32BEAt(movieData, offset + 12),
            MatrixA: BigEndianReader.ReadSingleBEAt(movieData, offset + 16),
            MatrixB: BigEndianReader.ReadSingleBEAt(movieData, offset + 20),
            MatrixC: BigEndianReader.ReadSingleBEAt(movieData, offset + 24),
            MatrixD: BigEndianReader.ReadSingleBEAt(movieData, offset + 28),
            TranslateX: BigEndianReader.ReadSingleBEAt(movieData, offset + 32),
            TranslateY: BigEndianReader.ReadSingleBEAt(movieData, offset + 36),
            ColorAdd: BigEndianReader.ReadUInt32BEAt(movieData, offset + 40),
            ColorMul: BigEndianReader.ReadUInt32BEAt(movieData, offset + 44));
    }
}
