namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One record from a movie's tag stream -- the region after the class/import
/// table's string pool, previously undecoded (see the historical remarks on <see
/// cref="AptMovie"/>). Confirmed universal shape (2026-08-19, 127 records across 3
/// real NHL overlay archives, 127/127 magic matches): a run of big-endian u32
/// offsets starting at the CONST file's <see cref="AptConstantFile.AptDataOffset"/>
/// (or a derived fallback when no CONST is available) gives each record's start;
/// every record begins <c>[type:u32][magic=0x09876543:u32][fieldA:u32][fieldB:u32]</c>
/// before its type-specific payload. <see cref="Type"/> is one of <see
/// cref="AptCharacterType"/>'s values in every real sample. <see cref="FieldA"/>/<see
/// cref="FieldB"/> are exposed raw because their meaning varies by type (for
/// <see cref="AptCharacterType.Shape"/> specifically, FieldA is the confirmed
/// geometry key -- see <see cref="AptShapeCharacter"/> -- but this base record makes
/// no claim about any other type's fields beyond what its own typed wrapper, if any
/// exists yet, confirms).
/// </summary>
public sealed record AptTagRecord(int Offset, int Length, AptCharacterType Type, uint FieldA, uint FieldB, byte[] RawData)
{
    public const uint Magic = 0x09876543;
}
