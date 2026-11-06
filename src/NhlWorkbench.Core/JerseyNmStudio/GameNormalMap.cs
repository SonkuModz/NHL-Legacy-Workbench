using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;

namespace NhlWorkbench.Core.JerseyNmStudio;

/// <summary>
/// What the game's own jersey normal maps look like, measured from the shipped files (not documented anywhere):
/// <list type="bullet">
/// <item>The original-style jerseys (2048x2048, DXT5) store ONE bump pattern in both the red and green channels,
/// centred near 140.9 / 140.4 with a spread (standard deviation) of about 22.5, blue is 0, and alpha is a constant 77
/// (checked on seven teams: alpha was exactly 77 on every pixel of every one).</item>
/// <item>The newer converted jerseys (1024x1024, BC5) are different: two channels centred near 127 / 131, spread about 15, no alpha.</item>
/// </list>
/// A normal map put into a DXT5 jersey slot from a converter that follows other conventions (a different alpha such as 118,
/// red/green centred near 81/87) looks fine as a picture but lights the jersey lopsidedly -- every thread tilts the same way.
/// <see cref="Fix"/> re-expresses such a map in the game's style.
/// </summary>
public static class GameNormalMap
{
    public sealed record Profile(double MeanRed, double MeanGreen, double StdRed, double StdGreen, byte Blue, byte Alpha);

    /// <summary>Averages over the stock DXT5 jersey normal maps (red 140.8-141.2, green 140.3-140.7, spread 22.2-23.2).</summary>
    public static Profile StockDxt5Jersey { get; } = new(140.9, 140.4, 22.5, 22.5, 0, 77);

    public sealed record Measurement(double MeanRed, double MeanGreen, double StdRed, double StdGreen, double MeanBlue, double MinAlpha, double MaxAlpha, double MeanAlpha);

    public sealed record Assessment(bool Applicable, bool MatchesGame, string Summary);

    public static Measurement Measure(Rgba32Image image)
    {
        int count = image.Width * image.Height;
        double[] mean = new double[4];
        int minAlpha = 255;
        int maxAlpha = 0;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                mean[c] += image.Pixels[(i * 4) + c];
            }

            minAlpha = Math.Min(minAlpha, image.Pixels[(i * 4) + 3]);
            maxAlpha = Math.Max(maxAlpha, image.Pixels[(i * 4) + 3]);
        }

        for (int c = 0; c < 4; c++)
        {
            mean[c] /= count;
        }

        double varRed = 0;
        double varGreen = 0;
        for (int i = 0; i < count; i++)
        {
            double dr = image.Pixels[i * 4] - mean[0];
            double dg = image.Pixels[(i * 4) + 1] - mean[1];
            varRed += dr * dr;
            varGreen += dg * dg;
        }

        return new Measurement(mean[0], mean[1], Math.Sqrt(varRed / count), Math.Sqrt(varGreen / count), mean[2], minAlpha, maxAlpha, mean[3]);
    }

    /// <summary>Does this decoded jersey normal map follow the game's DXT5 style? Not applicable to other formats (BC5 has no alpha to get wrong).</summary>
    public static Assessment Assess(Rgba32Image image, TextureFormat format)
    {
        if (format != TextureFormat.Dxt5)
        {
            return new Assessment(false, true, $"{format}: no fix needed -- only the DXT5 jersey normal maps have the alpha / centring quirk.");
        }

        Measurement m = Measure(image);
        Profile target = StockDxt5Jersey;
        var problems = new List<string>();

        if (m.MaxAlpha - m.MinAlpha > 2 || Math.Abs(m.MeanAlpha - target.Alpha) > 2)
        {
            problems.Add($"alpha is {m.MeanAlpha:F0} (the game uses a constant {target.Alpha})");
        }

        if (Math.Abs(m.MeanRed - target.MeanRed) > 8 || Math.Abs(m.MeanGreen - target.MeanGreen) > 8)
        {
            problems.Add($"red/green are centred at {m.MeanRed:F0}/{m.MeanGreen:F0} (the game's are about {target.MeanRed:F0}/{target.MeanGreen:F0}) so shading leans one way");
        }

        if (m.MeanBlue > 2)
        {
            problems.Add($"blue is {m.MeanBlue:F0} (the game uses 0)");
        }

        if (m.StdRed < target.StdRed * 0.6 || m.StdRed > target.StdRed * 1.6)
        {
            problems.Add($"bump strength is {m.StdRed:F0} (the game's is about {target.StdRed:F0})");
        }

        return problems.Count == 0
            ? new Assessment(true, true, "Matches the game's jersey normal-map style.")
            : new Assessment(true, false, "Differs from the game's style: " + string.Join("; ", problems) + ".");
    }

    /// <summary>
    /// A copy of <paramref name="source"/> in the game's DXT5 jersey style: alpha set to the game's constant, blue to 0, and red/green
    /// shifted so their centres match the game's (<paramref name="matchStrength"/> also stretches the bump detail to the game's spread,
    /// so the lighting has the same punch). The detail pattern itself is the source's own.
    /// </summary>
    public static Rgba32Image Fix(Rgba32Image source, bool matchStrength = true, Profile? target = null)
    {
        target ??= StockDxt5Jersey;
        Measurement m = Measure(source);
        double gainRed = matchStrength && m.StdRed > 0.5 ? target.StdRed / m.StdRed : 1.0;
        double gainGreen = matchStrength && m.StdGreen > 0.5 ? target.StdGreen / m.StdGreen : 1.0;

        var pixels = new byte[source.Pixels.Length];
        for (int i = 0; i < source.Width * source.Height; i++)
        {
            int o = i * 4;
            pixels[o] = ToByte(target.MeanRed + ((source.Pixels[o] - m.MeanRed) * gainRed));
            pixels[o + 1] = ToByte(target.MeanGreen + ((source.Pixels[o + 1] - m.MeanGreen) * gainGreen));
            pixels[o + 2] = target.Blue;
            pixels[o + 3] = target.Alpha;
        }

        return new Rgba32Image { Width = source.Width, Height = source.Height, Pixels = pixels };
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
