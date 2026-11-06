namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>
/// Maps a destination RX2 texture's own internal name to a <see cref="UniformSlotKind"/>. Unlike the
/// Jersey Importer's equivalent (which has to disambiguate a "font_" vs "jersey_" prefix), every
/// uniform category confirmed so far uses exactly one naming prefix per container (e.g. "pant_",
/// "sock_", "goaliehelmet_"), so a single suffix-only rule works for all of them: real names always
/// end in "_cm.Raster" / "_nm.Raster" / "_sm.Raster" (confirmed by decoding real pant/sock/goalie
/// helmet containers).
/// </summary>
public static class UniformSlotMapping
{
    public static UniformSlotKind? FromDestinationName(string textureEntryName)
    {
        if (string.IsNullOrEmpty(textureEntryName))
        {
            return null;
        }

        if (textureEntryName.EndsWith("_cm.Raster", StringComparison.OrdinalIgnoreCase))
        {
            return UniformSlotKind.ColorMap;
        }

        if (textureEntryName.EndsWith("_nm.Raster", StringComparison.OrdinalIgnoreCase))
        {
            return UniformSlotKind.NormalMap;
        }

        if (textureEntryName.EndsWith("_sm.Raster", StringComparison.OrdinalIgnoreCase))
        {
            return UniformSlotKind.SpecularMap;
        }

        return null;
    }
}
