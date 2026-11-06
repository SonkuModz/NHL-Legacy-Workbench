using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.JerseyNmStudio;
using Xunit;

namespace NhlWorkbench.Core.Tests.JerseyNmStudio;

public class HueSaturationAdjustmentTests
{
    [Fact]
    public void Apply_ZeroShiftFullSaturation_IsANoOp()
    {
        Rgba32Image image = MakeSolid(200, 80, 40, 255);

        Rgba32Image result = HueSaturationAdjustment.Apply(image, hueShiftDegrees: 0, saturationPercent: 100);

        AssertPixelApprox(result, 0, 200, 80, 40, 255, tolerance: 1);
    }

    [Fact]
    public void Apply_180DegreeShift_RotatesRedToCyan()
    {
        // Pure red (hue 0) shifted by 180 degrees should land on pure cyan (hue 180), same S/V.
        Rgba32Image image = MakeSolid(255, 0, 0, 255);

        Rgba32Image result = HueSaturationAdjustment.Apply(image, hueShiftDegrees: 180, saturationPercent: 100);

        AssertPixelApprox(result, 0, 0, 255, 255, 255, tolerance: 2);
    }

    [Fact]
    public void Apply_ZeroSaturation_DesaturatesToGray()
    {
        Rgba32Image image = MakeSolid(200, 80, 40, 255);

        Rgba32Image result = HueSaturationAdjustment.Apply(image, hueShiftDegrees: 0, saturationPercent: 0);

        // Fully desaturated -> R, G, and B must all equal each other (the original Value).
        Assert.Equal(result.Pixels[0], result.Pixels[1]);
        Assert.Equal(result.Pixels[1], result.Pixels[2]);
    }

    [Fact]
    public void Apply_PreservesAlphaUnchanged()
    {
        Rgba32Image image = MakeSolid(10, 20, 30, 77);

        Rgba32Image result = HueSaturationAdjustment.Apply(image, hueShiftDegrees: 90, saturationPercent: 150);

        Assert.Equal(77, result.Pixels[3]);
    }

    [Fact]
    public void ComputeAverageHueDegrees_PureYellowImage_ReturnsApproximately60()
    {
        Rgba32Image image = MakeSolid(255, 255, 0, 255); // pure yellow, hue = 60

        double hue = HueSaturationAdjustment.ComputeAverageHueDegrees(image);

        Assert.InRange(hue, 58, 62);
    }

    private static void AssertPixelApprox(Rgba32Image image, int pixelIndex, byte r, byte g, byte b, byte a, int tolerance)
    {
        int offset = pixelIndex * 4;
        Assert.InRange(image.Pixels[offset], r - tolerance, r + tolerance);
        Assert.InRange(image.Pixels[offset + 1], g - tolerance, g + tolerance);
        Assert.InRange(image.Pixels[offset + 2], b - tolerance, b + tolerance);
        Assert.Equal(a, image.Pixels[offset + 3]);
    }

    private static Rgba32Image MakeSolid(byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new Rgba32Image { Width = 4, Height = 4, Pixels = pixels };
    }
}
