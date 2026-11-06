using NhlWorkbench.Core.Dds;

namespace NhlWorkbench.Core.EquipmentPreview;

/// <summary>
/// Paints database zone colors onto the equipment zone-mask textures. Each mask file packs three
/// zones, one per color channel: a pixel belongs to zone <c>fileIndex*3 + channel</c> when that
/// channel is bright (&gt;128) and the other two are dark (&lt;64) -- the same rule Heck's editor
/// uses. Zone-color order is the database's zone 1..9.
/// </summary>
public static class EquipmentZoneColorizer
{
    private const int Bright = 128;
    private const int Dark = 64;

    public static Rgba32Image? Colorize(
        IReadOnlyList<Rgba32Image?> zoneMasks,
        IReadOnlyList<(byte R, byte G, byte B)> zoneColors,
        Rgba32Image? detail)
    {
        Rgba32Image? first = zoneMasks.FirstOrDefault(m => m is not null);
        if (first is null)
        {
            return null;
        }

        int width = first.Width;
        int height = first.Height;
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        for (int fileIndex = 0; fileIndex < zoneMasks.Count; fileIndex++)
        {
            Rgba32Image? mask = zoneMasks[fileIndex];
            if (mask is null || mask.Width != width || mask.Height != height)
            {
                continue;
            }

            for (int channel = 0; channel < 3; channel++)
            {
                int zone = (fileIndex * 3) + channel;
                if (zone >= zoneColors.Count)
                {
                    break;
                }

                (byte r, byte g, byte b) = zoneColors[zone];
                int c1 = (channel + 1) % 3;
                int c2 = (channel + 2) % 3;

                for (int p = 0; p < width * height; p++)
                {
                    int o = p * 4;
                    if (mask.Pixels[o + channel] > Bright && mask.Pixels[o + c1] < Dark && mask.Pixels[o + c2] < Dark)
                    {
                        pixels[o] = r;
                        pixels[o + 1] = g;
                        pixels[o + 2] = b;
                    }
                }
            }
        }

        if (detail is not null)
        {
            BlendDetail(pixels, width, height, detail);
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>Alpha-composites the detail overlay (logos, stitching) over the colored base, resampling nearest-neighbour if sizes differ.</summary>
    private static void BlendDetail(byte[] pixels, int width, int height, Rgba32Image detail)
    {
        for (int y = 0; y < height; y++)
        {
            int dy = detail.Height == height ? y : y * detail.Height / height;
            for (int x = 0; x < width; x++)
            {
                int dx = detail.Width == width ? x : x * detail.Width / width;
                int s = ((dy * detail.Width) + dx) * 4;
                int d = ((y * width) + x) * 4;
                float a = detail.Pixels[s + 3] / 255f;
                for (int c = 0; c < 3; c++)
                {
                    pixels[d + c] = (byte)Math.Clamp((detail.Pixels[s + c] * a) + (pixels[d + c] * (1f - a)), 0f, 255f);
                }
            }
        }
    }
}
