using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.Formats.Xpr;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth coverage for the RefPack + XPR2 pipeline against the real
/// <c>d24.big</c> entry "96" -- the St. Louis Blues team logo, independently confirmed as
/// 128x128 A8R8G8B8 (1 mip) by NHL Modding Studio's own reader rendering the identical
/// image. The expected pixel values below were read from OUR decode and visually matched
/// against that reference render, so they lock in the whole chain: BIGF entry extraction,
/// RefPack decompression (itself validated byte-for-byte against QuickBMS's COMP_DK2 on
/// this exact entry), XPR2 header/descriptor parsing, XDK raw-pixel untiling, and the
/// big-endian-ARGB -> BGRA channel reorder.
/// </summary>
public class XprContainerTests
{
    private static byte[]? GetRealEntry96()
    {
        if (!SampleFileCatalog.BigSampleAvailable)
        {
            return null;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.BigSampleFile);
        BigfArchive archive = BigfArchive.Parse(data, SampleFileCatalog.BigSampleFile).Value!;
        return archive.GetEntryData("96");
    }

    [Fact]
    public void RefPack_Decompress_RealEntry_MatchesDeclaredSizeAndXprMagic()
    {
        byte[]? entry = GetRealEntry96();
        if (entry is null)
        {
            return;
        }

        Assert.True(RefPack.IsRefPack(entry));

        Result<byte[]> decompressed = RefPack.Decompress(entry);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Equal(65632, decompressed.Value!.Length);
        Assert.True(XprContainer.IsXpr(decompressed.Value));
    }

    [Fact]
    public void RefPack_CompressThenDecompress_RoundTripsArbitraryData()
    {
        var data = new byte[100_001];
        new Random(11).NextBytes(data);

        Result<byte[]> compressed = RefPack.Compress(data);
        Assert.True(compressed.IsSuccess, compressed.Error);

        Result<byte[]> decompressed = RefPack.Decompress(compressed.Value!);
        Assert.True(decompressed.IsSuccess, decompressed.Error);
        Assert.Equal(data, decompressed.Value);
    }

    [Fact]
    public void Parse_RealEntry_DecodesTheBluesLogoWithKnownDimensionsAndPixels()
    {
        byte[]? entry = GetRealEntry96();
        if (entry is null)
        {
            return;
        }

        Result<XprContainer> result = XprContainer.Parse(entry, "96");
        Assert.True(result.IsSuccess, result.Error);

        XprContainer container = result.Value!;
        Assert.Equal(ContainerFormat.Xpr, container.Format);
        Assert.Single(container.Textures);

        TextureEntry texture = container.Textures[0];
        Assert.Equal(128, texture.Width);
        Assert.Equal(128, texture.Height);
        Assert.Equal(TextureFormat.Argb8888, texture.Format);
        Assert.Equal(1, texture.MipCount);
        Assert.Equal(128 * 128 * 4, texture.LinearPixelData.Length);

        // Corner pixels are fully transparent; the note body is Blues navy; (40,30) sits in
        // the soft drop shadow. Values are in BGRA memory order (TextureFormat.Argb8888 maps
        // to DXGI B8G8R8A8) and were verified pixel-exact against NHL Modding Studio's
        // independent decode of this same texture (16381/16384 exact across the image, the
        // remainder within that reference PNG's export rounding).
        AssertPixel(texture, 2, 2, b: 0x00, g: 0x00, r: 0x00, a: 0x00);
        AssertPixel(texture, 52, 88, b: 0x39, g: 0x1F, r: 0x12, a: 0xFF);
        AssertPixel(texture, 64, 64, b: 0x6D, g: 0x49, r: 0x36, a: 0xFF);
        AssertPixel(texture, 40, 30, b: 0x09, g: 0x00, r: 0x00, a: 0xCA);
    }

    [Fact]
    public void ReplaceThenSave_SameShape_RoundTripsThroughRefPackAndReparses()
    {
        byte[]? entry = GetRealEntry96();
        if (entry is null)
        {
            return;
        }

        XprContainer container = XprContainer.Parse(entry, "96").Value!;
        TextureEntry original = container.Textures[0];

        // Replace with a synthetic same-shape image (solid semi-transparent teal).
        var pixels = new byte[original.LinearPixelData.Length];
        for (int i = 0; i + 4 <= pixels.Length; i += 4)
        {
            pixels[i] = 180; pixels[i + 1] = 160; pixels[i + 2] = 20; pixels[i + 3] = 200;
        }

        var replacement = new TextureEntry
        {
            Name = original.Name,
            Index = 0,
            Width = original.Width,
            Height = original.Height,
            Format = original.Format,
            MipCount = 1,
            LinearPixelData = pixels,
        };

        container.ReplaceTexture(0, replacement);
        Assert.True(container.GetPatchCapabilities().HasFlag(PatchCapabilities.InPlace));

        byte[] saved = container.Save(PatchStrategy.InPlace);
        Assert.True(RefPack.IsRefPack(saved), "Saved entry should still be RefPack-wrapped like the original.");

        XprContainer reparsed = XprContainer.Parse(saved, "96").Value!;
        Assert.Equal(original.Width, reparsed.Textures[0].Width);
        Assert.Equal(original.Height, reparsed.Textures[0].Height);
        Assert.Equal(pixels, reparsed.Textures[0].LinearPixelData);
    }

    [Fact]
    public void Save_DifferentShapeReplacement_ThrowsWithClearMessage()
    {
        byte[]? entry = GetRealEntry96();
        if (entry is null)
        {
            return;
        }

        XprContainer container = XprContainer.Parse(entry, "96").Value!;
        var wrongShape = new TextureEntry
        {
            Name = "96",
            Index = 0,
            Width = 64,
            Height = 64,
            Format = TextureFormat.Argb8888,
            MipCount = 1,
            LinearPixelData = new byte[64 * 64 * 4],
        };

        container.ReplaceTexture(0, wrongShape);
        Assert.Equal(PatchCapabilities.None, container.GetPatchCapabilities());
        Assert.Throws<NotSupportedException>(() => container.Save(PatchStrategy.InPlace));
    }

    [Fact]
    public void Classify_RealEntry_ReturnsXpr()
    {
        byte[]? entry = GetRealEntry96();
        if (entry is null)
        {
            return;
        }

        Assert.Equal(BigEntryFormat.Xpr, BigEntryClassifier.Classify(entry));
    }

    private static void AssertPixel(TextureEntry texture, int x, int y, byte b, byte g, byte r, byte a)
    {
        int i = (y * texture.Width + x) * 4;
        Assert.Equal(b, texture.LinearPixelData[i]);
        Assert.Equal(g, texture.LinearPixelData[i + 1]);
        Assert.Equal(r, texture.LinearPixelData[i + 2]);
        Assert.Equal(a, texture.LinearPixelData[i + 3]);
    }
}
