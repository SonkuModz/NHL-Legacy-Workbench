namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// The <see cref="AptCharacterType.EditText"/> tag -- confirmed 2026-08-19 against 6
/// real records across overlay_0.big and overlay_53.big: a bounding rect (4 floats,
/// consistently starting at (-2,-2) in every real sample -- a small authoring-margin
/// convention, not coincidence), <see cref="FontSize"/> and <see cref="Alignment"/>
/// as small integers, <see cref="Color"/> as a packed 0xAARRGGBB value (every real
/// sample decodes to a plausible opaque UI color: black, white, and various grays),
/// <see cref="Leading"/> as a float, and a trailing string offset that resolves to
/// the field's real static text content with an EXACT byte-offset match against the
/// movie's independently-scanned string table (e.g. resolves to literally "ENERGY
/// GAINED" at the offset this field names). The external community research on this
/// format (References/aptresearch/APT_TEXT.md) found real NCAA14 EditText characters
/// are always empty (dynamic score/clock/name content is supplied by ActionScript at
/// runtime, never stored statically) -- so an empty <see cref="Text"/> here is
/// expected and correct for genuinely dynamic fields, not a parse failure.
/// </summary>
public sealed record AptEditTextCharacter(
    AptTagRecord Tag,
    float Left,
    float Top,
    float Width,
    float Height,
    int FontSize,
    int Alignment,
    uint Color,
    float Leading,
    string Text)
{
    public byte Alpha => (byte)(Color >> 24);

    public byte Red => (byte)(Color >> 16);

    public byte Green => (byte)(Color >> 8);

    public byte Blue => (byte)Color;

    public static AptEditTextCharacter? TryParse(AptTagRecord tag, byte[] movieData)
    {
        if (tag.Type != AptCharacterType.EditText || tag.RawData.Length < 64)
        {
            return null;
        }

        byte[] d = tag.RawData;
        float left = Utilities.BigEndianReader.ReadSingleBEAt(d, 16);
        float top = Utilities.BigEndianReader.ReadSingleBEAt(d, 20);
        float width = Utilities.BigEndianReader.ReadSingleBEAt(d, 24);
        float height = Utilities.BigEndianReader.ReadSingleBEAt(d, 28);
        int fontSize = Utilities.BigEndianReader.ReadInt32BEAt(d, 32);
        int alignment = Utilities.BigEndianReader.ReadInt32BEAt(d, 36);
        uint color = Utilities.BigEndianReader.ReadUInt32BEAt(d, 40);
        float leading = Utilities.BigEndianReader.ReadSingleBEAt(d, 44);
        int textOffset = Utilities.BigEndianReader.ReadInt32BEAt(d, 60);

        string text = textOffset >= 0 && textOffset < movieData.Length
            ? AptStringScanner.ReadPrintableRun(movieData, textOffset)
            : string.Empty;

        return new AptEditTextCharacter(tag, left, top, width, height, fontSize, alignment, color, leading, text);
    }
}
