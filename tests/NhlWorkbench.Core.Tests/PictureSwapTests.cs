using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class PictureSwapTests
{
    private static readonly DdsCodec Codec = new();

    private static Rgba32Image Solid(int width, int height, byte red, byte green, byte blue, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = alpha;
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }

    private static (byte R, byte G, byte B, byte A) At(Rgba32Image image, int x, int y)
    {
        int i = ((y * image.Width) + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }

    [Fact]
    public void WidePictureIsCenteredWithTransparentBandsThatKeepItsColor()
    {
        Rgba32Image fitted = Codec.FitInside(Solid(400, 100, 200, 20, 40), 256, 256).Value!;

        Assert.Equal(256, fitted.Width);
        Assert.Equal(256, fitted.Height);
        Assert.Equal((byte)255, At(fitted, 128, 128).A);
        Assert.Equal((byte)200, At(fitted, 128, 128).R);
        Assert.Equal((200, 20, 40, 0), (At(fitted, 128, 5).R, At(fitted, 128, 5).G, At(fitted, 128, 5).B, (int)At(fitted, 128, 5).A));
    }

    [Fact]
    public void PictureOfTheRightSizeIsLeftAlone()
    {
        Rgba32Image picture = Solid(64, 64, 1, 2, 3, 200);

        Assert.Equal(picture.Pixels, Codec.FitInside(picture, 64, 64).Value!.Pixels);
    }

    [Fact]
    public void SmallPictureIsEnlargedToFit()
    {
        Rgba32Image fitted = Codec.FitInside(Solid(16, 32, 90, 90, 90), 256, 256).Value!;

        Assert.Equal((byte)255, At(fitted, 128, 3).A);
        Assert.Equal((byte)0, At(fitted, 5, 128).A);
    }

    [Fact]
    public void FullyTransparentPictureStillWorks()
    {
        Assert.True(Codec.FitInside(Solid(8, 8, 0, 0, 0, 0), 16, 32).IsSuccess);
    }

    [Fact]
    public void EmptyPictureIsRefused()
    {
        Assert.False(Codec.FitInside(new Rgba32Image { Width = 0, Height = 0, Pixels = [] }, 16, 16).IsSuccess);
    }

    [Fact]
    public void JunkFileIsRefused()
    {
        Assert.False(PictureSwap.Replace([1, 2, 3, 4], "junk", Solid(4, 4, 0, 0, 0), Codec).IsSuccess);
    }

    [Fact]
    public void RealCenterIceLogoTakesAPictureAndKeepsItsLayout()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = game.ReadDecoded(game.Find("rendering/icesurface/centerlogo_0_cm.rpsgl")!.Winner).Value!;

        Result<byte[]> swapped = PictureSwap.Replace(original, "centerlogo_0", Solid(2000, 1000, 230, 30, 40), Codec);

        Assert.True(swapped.IsSuccess, swapped.Error);
        Assert.Equal(original.Length, swapped.Value!.Length);

        RpsglContainer before = RpsglReader.Parse(original, "before").Value!;
        RpsglContainer after = RpsglReader.Parse(swapped.Value, "after").Value!;
        TextureEntry oldTexture = PictureSwap.MainTexture(before)!;
        TextureEntry newTexture = PictureSwap.MainTexture(after)!;
        Assert.Equal((oldTexture.Width, oldTexture.Height, oldTexture.Format, oldTexture.MipCount), (newTexture.Width, newTexture.Height, newTexture.Format, newTexture.MipCount));

        Rgba32Image decoded = Codec.Decode(newTexture.LinearPixelData, newTexture.Width, newTexture.Height, newTexture.Format).Value!;
        (byte red, byte green, _, byte alpha) = At(decoded, decoded.Width / 2, decoded.Height / 2);
        Assert.InRange(red, 220, 240);
        Assert.InRange(green, 20, 40);
        Assert.Equal((byte)255, alpha);
        Assert.Equal((byte)0, At(decoded, decoded.Width / 2, 20).A);

        // Only the pixel data moved: the header in front of it is byte-for-byte the same.
        int firstDifference = original.AsSpan().CommonPrefixLength(swapped.Value);
        Assert.True(firstDifference >= original.Length - oldTexture.LinearPixelData.Length, $"Changed at {firstDifference} of {original.Length}.");
    }
}
