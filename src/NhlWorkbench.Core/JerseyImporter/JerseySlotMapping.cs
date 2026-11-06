namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>
/// Pure, no-I/O translation between the two different naming schemes a jersey texture slot is
/// known by: the source DDS filename suffix (confirmed by decoding real files: base/_1/_2/_3/_4/_5)
/// and the destination RX2 container's own texture name (confirmed self-documenting: font_..._cm/
/// font_..._nm/font_..._sm/jersey_..._cm/jersey_..._sm/jersey_..._0_nm). Matching the destination
/// name is deliberately substring/EndsWith-based rather than exact-equals, to tolerate the one
/// confirmed irregularity: the jersey normal map's name has an extra "_0_" segment
/// (jersey_..._0_nm.Raster) that the other 5 names don't have.
/// </summary>
public static class JerseySlotMapping
{
    public static JerseySlotKind? FromSourceSuffix(string suffix) => suffix switch
    {
        "" => JerseySlotKind.FontCm,
        "_1" => JerseySlotKind.FontNm,
        "_2" => JerseySlotKind.FontSm,
        "_3" => JerseySlotKind.JerseyCm,
        "_4" => JerseySlotKind.JerseySm,
        "_5" => JerseySlotKind.JerseyNm,
        _ => null,
    };

    public static JerseySlotKind? FromDestinationName(string textureEntryName)
    {
        if (string.IsNullOrEmpty(textureEntryName))
        {
            return null;
        }

        bool isFont = textureEntryName.Contains("font_", StringComparison.OrdinalIgnoreCase);
        bool isJersey = textureEntryName.Contains("jersey_", StringComparison.OrdinalIgnoreCase);

        if (!isFont && !isJersey)
        {
            return null;
        }

        bool endsCm = textureEntryName.EndsWith("_cm.Raster", StringComparison.OrdinalIgnoreCase);
        bool endsNm = textureEntryName.EndsWith("_nm.Raster", StringComparison.OrdinalIgnoreCase);
        bool endsSm = textureEntryName.EndsWith("_sm.Raster", StringComparison.OrdinalIgnoreCase);

        if (isFont)
        {
            if (endsCm)
            {
                return JerseySlotKind.FontCm;
            }

            if (endsNm)
            {
                return JerseySlotKind.FontNm;
            }

            if (endsSm)
            {
                return JerseySlotKind.FontSm;
            }

            return null;
        }

        if (endsCm)
        {
            return JerseySlotKind.JerseyCm;
        }

        if (endsNm)
        {
            return JerseySlotKind.JerseyNm;
        }

        if (endsSm)
        {
            return JerseySlotKind.JerseySm;
        }

        return null;
    }
}
