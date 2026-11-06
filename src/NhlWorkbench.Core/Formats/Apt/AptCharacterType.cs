namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// The character-type tag at offset+0 of every record in an Apt movie's tag stream.
/// Confirmed against real NHL Legacy overlay archives (2026-08-19): 127 tag records
/// across 3 independent movies, every one carrying the <see cref="AptTagRecord.Magic"/>
/// sentinel and a type value from exactly this set (1, 2, 3, 5, 7, 9 observed; 4, 8, 10
/// never seen but assigned by the same external reference this enum is transcribed
/// from). This numbering is NOT NHL-specific -- it matches the community's
/// independently reverse-engineered EA APT format used by NCAA Football 14 and other
/// EA Sports titles of the same "Ion" UI engine generation (see
/// References/aptresearch/HISTORICAL_ARCHIVE.md §3, sourced from that project's own
/// direct byte-level research, itself citing community discord research from the
/// format's original NCAA/Madden reverse-engineers). The exact field layouts below
/// were independently re-derived and verified against real NHL bytes, not copied
/// blind -- see each character record type's own remarks for what's confirmed here
/// versus only in the external reference.
/// </summary>
public enum AptCharacterType
{
    Shape = 1,
    EditText = 2,
    Font = 3,
    Button = 4,
    Sprite = 5,
    Image = 7,
    Morph = 8,
    Movie = 9,
    Text = 10,
}
