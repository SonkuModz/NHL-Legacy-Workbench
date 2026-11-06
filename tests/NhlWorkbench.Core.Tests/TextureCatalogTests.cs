using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class TextureCatalogTests
{
    private static readonly DdsCodec Codec = new();

    private static Rgba32Image Solid(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 1] = green;
            pixels[i + 2] = blue;
            pixels[i + 3] = 255;
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }

    [Theory]
    [InlineData("rendering/icesurface/centerlogo_0_cm.rpsgl", "Ice and rink")]
    [InlineData("rendering/jersey/jersey_0_0_2_0_0_0.rpsgl", "Uniforms")]
    [InlineData("rendering/logo/texlib_0_0_0.rpsgl", "Logos")]
    [InlineData("rendering/somethingnew/a.rpsgl", "Other")]
    [InlineData("loose.rpsgl", "Other")]
    public void FoldersMapToCategories(string path, string category)
    {
        Assert.Equal(category, TextureCatalog.CategoryFor(path));
        Assert.Contains(category, TextureCatalog.Categories);
    }

    [Fact]
    public void PreviewUsesASmallMipButNeverOneTooSmall()
    {
        DecodedDdsTexture texture = Codec.EncodeToFormat(Solid(1024, 512, 200, 30, 40), TextureFormat.Dxt3, 11).Value!;

        Rgba32Image preview = Codec.DecodeForPreview(texture.LinearData, 1024, 512, TextureFormat.Dxt3, 11, 128).Value!;

        Assert.Equal((128, 64), (preview.Width, preview.Height));
        Assert.InRange(preview.Pixels[0], 190, 210);
    }

    [Fact]
    public void PreviewOfASingleMipTextureIsTheWholePicture()
    {
        DecodedDdsTexture texture = Codec.EncodeToFormat(Solid(64, 64, 1, 2, 3), TextureFormat.Dxt1, 1).Value!;

        Rgba32Image preview = Codec.DecodeForPreview(texture.LinearData, 64, 64, TextureFormat.Dxt1, 1, 128).Value!;

        Assert.Equal((64, 64), (preview.Width, preview.Height));
    }

    [Fact]
    public void PreviewOfTruncatedDataFailsWithoutThrowing()
    {
        Assert.False(Codec.DecodeForPreview(new byte[8], 1024, 1024, TextureFormat.Dxt3, 11, 128).IsSuccess);
    }

    [Fact]
    public void RealCatalogListsEveryTextureFileAndLeavesOutTheEmptyOnes()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;

        TextureCatalog catalog = TextureCatalog.Build(game);

        Assert.Equal(14651, catalog.Files.Count(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal)));
        Assert.Equal(0, catalog.UnreadableFiles);
        Assert.Equal(26101, catalog.Files.Where(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal)).Sum(f => f.Textures.Count));
        Assert.Contains(catalog.Files, f => f.Path == "fe/ion/artassets/teamlogos/t0.big" && f.Category == "Menu art");
        Assert.DoesNotContain(catalog.Files, f => f.Category == "Other" && f.Path.Contains("/jersey/", StringComparison.Ordinal));
    }

    [Fact]
    public void RawTexturesAreReadInFullWithTheirRealColors()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        const string path = "rendering/effects/powerring_lighting_0_0.rpsgl";
        byte[] original = game.ReadDecoded(game.Find(path)!.Winner).Value!;

        TextureEntry texture = PictureSwap.MainTexture(RpsglReader.Parse(original, path).Value!)!;
        Assert.Equal(TextureFormat.Argb8888, texture.Format);
        Assert.Equal(32 * 32 * 4, texture.LinearPixelData.Length);

        // The pixels end the file, stored as alpha, red, green, blue.
        byte[] stored = original[^(32 * 32 * 4)..];
        Rgba32Image decoded = Codec.Decode(texture.LinearPixelData, texture.Width, texture.Height, texture.Format).Value!;
        Assert.Equal(new[] { stored[1], stored[2], stored[3], stored[0] }, decoded.Pixels[..4]);

        // And nothing is lost or reordered on the way back out.
        ExportedDds dds = TextureDds.ExportMain(original, path, Codec).Value!;
        Assert.Equal(original, TextureDds.Import(original, path, dds.Bytes, Codec).Value);
    }

    [Fact]
    public void ChoosingASecondTextureLeavesTheOtherAloneAndEditsAddUp()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        const string path = "rendering/czsock/texlib_0.rpsgl";
        byte[] original = game.ReadDecoded(game.Find(path)!.Winner).Value!;
        RpsglContainer before = RpsglReader.Parse(original, path).Value!;
        Assert.Equal(2, before.Textures.Count);
        TextureEntry first = before.Textures[0];
        TextureEntry second = before.Textures[1];

        byte[] once = PictureSwap.Replace(original, path, Solid(64, 64, 230, 30, 40), Codec, second.Index).Value!;
        RpsglContainer afterOne = RpsglReader.Parse(once, path).Value!;
        Assert.Equal(original.Length, once.Length);
        Assert.Equal(first.LinearPixelData, afterOne.Textures[0].LinearPixelData);
        Assert.NotEqual(second.LinearPixelData, afterOne.Textures[1].LinearPixelData);

        byte[] twice = PictureSwap.Replace(once, path, Solid(64, 64, 20, 200, 40), Codec, first.Index).Value!;
        RpsglContainer afterTwo = RpsglReader.Parse(twice, path).Value!;
        Assert.NotEqual(first.LinearPixelData, afterTwo.Textures[0].LinearPixelData);
        Assert.Equal(afterOne.Textures[1].LinearPixelData, afterTwo.Textures[1].LinearPixelData);
    }

    [Fact]
    public void AMissingTextureIndexIsRefused()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        const string path = "rendering/czsock/texlib_0.rpsgl";
        byte[] original = game.ReadDecoded(game.Find(path)!.Winner).Value!;

        Assert.False(PictureSwap.Replace(original, path, Solid(8, 8, 1, 1, 1), Codec, 99).IsSuccess);
        Assert.False(TextureDds.ExportMain(original, path, Codec, 99).IsSuccess);
    }

    [Theory]
    [InlineData(TextureFormat.Dxt1)]
    [InlineData(TextureFormat.Dxt3)]
    [InlineData(TextureFormat.Dxt5)]
    [InlineData(TextureFormat.Argb8888)]
    public void PicturesKeepTheirColorsInEveryFormatTheGameUses(TextureFormat format)
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TextureFile file = TextureCatalog.Build(game).Files.First(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal) && f.Textures.Count == 1 && f.Main.Format == format && f.Main.Width >= 32 && f.Main.Height >= 32);
        byte[] original = game.ReadDecoded(game.Find(file.Path)!.Winner).Value!;

        byte[] swapped = PictureSwap.Replace(original, file.Path, Solid(file.Main.Width, file.Main.Height, 230, 30, 40), Codec).Value!;
        TextureEntry result = PictureSwap.MainTexture(RpsglReader.Parse(swapped, file.Path).Value!)!;
        Rgba32Image decoded = Codec.Decode(result.LinearPixelData, result.Width, result.Height, result.Format).Value!;

        Assert.Equal(original.Length, swapped.Length);
        int center = ((decoded.Height / 2 * decoded.Width) + (decoded.Width / 2)) * 4;
        Assert.InRange(decoded.Pixels[center], 220, 240);
        Assert.InRange(decoded.Pixels[center + 1], 20, 40);
        Assert.InRange(decoded.Pixels[center + 2], 30, 50);
    }
}
