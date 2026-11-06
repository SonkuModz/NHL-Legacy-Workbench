using NhlWorkbench.Core.Dds;

namespace NhlWorkbench.Core.JerseyNmStudio;

/// <summary>
/// Pure per-pixel HSV hue/saturation transform. Deliberately format-agnostic (operates on
/// already-decoded RGBA8, same as every other DdsCodec caller) -- it has no opinion on whether
/// the source pixels represent a photographic color or, as with this feature's actual use case
/// (jersey Normal Maps), packed tangent-space surface data. Applying a hue rotation to NM data
/// does not "recolor" it in any physically meaningful sense; it re-maps the stored channel
/// values, which is exactly what this feature is deliberately for. See JerseyNmStudioViewModel
/// for the caller-side context on that tradeoff.
/// </summary>
public static class HueSaturationAdjustment
{
    /// <summary>
    /// Rotates hue by <paramref name="hueShiftDegrees"/> (wraps at ±180) and scales
    /// saturation by <paramref name="saturationPercent"/>/100 (0 = grayscale, 100 = unchanged,
    /// 200 = double intensity, clamped to that range). Alpha passes through untouched.
    /// </summary>
    public static Rgba32Image Apply(Rgba32Image image, double hueShiftDegrees, double saturationPercent)
    {
        double saturationScale = Math.Clamp(saturationPercent / 100.0, 0.0, 2.0);
        double hueShift = ((hueShiftDegrees % 360.0) + 360.0) % 360.0;

        byte[] source = image.Pixels;
        var result = new byte[source.Length];

        for (int i = 0; i < source.Length; i += 4)
        {
            (double h, double s, double v) = RgbToHsv(source[i], source[i + 1], source[i + 2]);

            h = (h + hueShift) % 360.0;
            s = Math.Clamp(s * saturationScale, 0.0, 1.0);

            (byte r, byte g, byte b) = HsvToRgb(h, s, v);
            result[i] = r;
            result[i + 1] = g;
            result[i + 2] = b;
            result[i + 3] = source[i + 3];
        }

        return new Rgba32Image { Width = image.Width, Height = image.Height, Pixels = result };
    }

    /// <summary>
    /// Circular mean hue (in degrees) across every pixel, weighted by that pixel's saturation so
    /// low-saturation (near-gray) pixels -- common in NM data -- don't dominate the average with
    /// an effectively-arbitrary hue. Used by the "Shift to Yellow Tone" preset to compute how far
    /// a given texture's current color actually is from yellow (60°), rather than applying a
    /// fixed, texture-agnostic offset.
    /// </summary>
    public static double ComputeAverageHueDegrees(Rgba32Image image)
    {
        double sumX = 0, sumY = 0;
        byte[] source = image.Pixels;

        for (int i = 0; i < source.Length; i += 4)
        {
            (double h, double s, double _) = RgbToHsv(source[i], source[i + 1], source[i + 2]);
            double radians = h * Math.PI / 180.0;
            sumX += Math.Cos(radians) * s;
            sumY += Math.Sin(radians) * s;
        }

        if (sumX == 0 && sumY == 0)
        {
            return 0.0;
        }

        double angle = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
        return angle < 0 ? angle + 360.0 : angle;
    }

    private static (double H, double S, double V) RgbToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h;
        if (delta == 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (h < 0)
        {
            h += 360.0;
        }

        double s = max == 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return (
            (byte)Math.Clamp((r + m) * 255.0, 0, 255),
            (byte)Math.Clamp((g + m) * 255.0, 0, 255),
            (byte)Math.Clamp((b + m) * 255.0, 0, 255));
    }
}
