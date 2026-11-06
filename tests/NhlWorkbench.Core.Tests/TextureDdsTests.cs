using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class TextureDdsTests
{
    private const string CenterLogo = "rendering/icesurface/centerlogo_0_cm.rpsgl";
    private static readonly DdsCodec Codec = new();

    private static byte[] OriginalLogo(GameFileIndex game) => game.ReadDecoded(game.Find(CenterLogo)!.Winner).Value!;

    private static TextureEntry MainOf(byte[] rpsgl) => PictureSwap.MainTexture(RpsglReader.Parse(rpsgl, "test").Value!)!;

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

    private static byte[] MakeDds(Rgba32Image image, TextureFormat format, int mips)
    {
        DecodedDdsTexture encoded = Codec.EncodeToFormat(image, format, mips).Value!;
        return Codec.WrapAsDds(encoded.LinearData, encoded.Width, encoded.Height, format, mips).Value!;
    }

    [Theory]
    [InlineData("rink/logo:1", "rink_logo_1")]
    [InlineData("..\\..\\evil", "evil")]
    [InlineData("plain-name_2.x", "plain-name_2.x")]
    [InlineData("...", "")]
    public void FileNamesCannotLeaveTheFolder(string name, string expected)
    {
        Assert.Equal(expected, TextureDds.SafeFileName(name));
    }

    [Fact]
    public void SeveralTexturesGetNumberedNames()
    {
        var dds = new ExportedDds("a/b", 3, TextureFormat.Dxt3, 4, 4, 1, []);

        Assert.Equal("jersey.dds", dds.FileName("jersey", isOnlyTexture: true));
        Assert.Equal("jersey_03_a_b.dds", dds.FileName("jersey", isOnlyTexture: false));
    }

    [Fact]
    public void OnlyRealDdsFilesAreRecognized()
    {
        Assert.True(TextureDds.IsDds("DDS |"u8));
        Assert.False(TextureDds.IsDds("\x89PNG"u8));
        Assert.False(TextureDds.IsDds([]));
    }

    [Fact]
    public void JunkIsRefusedWithoutThrowing()
    {
        Assert.False(TextureDds.ExportAll([1, 2, 3], "junk", Codec).IsSuccess);
        Assert.False(TextureDds.ExportMain([1, 2, 3], "junk", Codec).IsSuccess);
        Assert.False(TextureDds.Import([1, 2, 3], "junk", "DDS junk"u8.ToArray(), Codec).IsSuccess);
    }

    [Fact]
    public void RealLogoExportsWithTheGamesOwnBytes()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = OriginalLogo(game);
        TextureEntry texture = MainOf(original);

        ExportedDds exported = TextureDds.ExportMain(original, CenterLogo, Codec).Value!;
        DecodedDdsTexture read = Codec.ReadDdsFile(exported.Bytes).Value!;

        Assert.Equal((texture.Format, texture.Width, texture.Height, texture.MipCount), (read.Format, read.Width, read.Height, read.MipCount));
        Assert.Equal(texture.LinearPixelData, read.LinearData);
    }

    [Fact]
    public void ExportingAndImportingARealLogoGivesBackTheSameFile()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = OriginalLogo(game);
        ExportedDds exported = TextureDds.ExportMain(original, CenterLogo, Codec).Value!;

        Result<byte[]> imported = TextureDds.Import(original, CenterLogo, exported.Bytes, Codec);

        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Empty(imported.Warnings);
        Assert.Equal(original, imported.Value);
    }

    [Fact]
    public void MatchingDdsGoesInWithoutRecompression()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = OriginalLogo(game);
        TextureEntry texture = MainOf(original);
        byte[] dds = MakeDds(Solid(texture.Width, texture.Height, 230, 30, 40), texture.Format, texture.MipCount);

        byte[] imported = TextureDds.Import(original, CenterLogo, dds, Codec).Value!;

        Assert.Equal(original.Length, imported.Length);
        Assert.Equal(Codec.ReadDdsFile(dds).Value!.LinearData, MainOf(imported).LinearPixelData);
    }

    [Fact]
    public void DdsWithTooFewMipsGetsTheTextureMipCountAndAWarning()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = OriginalLogo(game);
        TextureEntry texture = MainOf(original);
        byte[] dds = MakeDds(Solid(texture.Width, texture.Height, 230, 30, 40), texture.Format, 1);

        Result<byte[]> imported = TextureDds.Import(original, CenterLogo, dds, Codec);

        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Single(imported.Warnings);
        Assert.Equal(original.Length, imported.Value!.Length);
        Assert.Equal(texture.MipCount, MainOf(imported.Value).MipCount);
    }

    [Fact]
    public void DdsOfAnotherSizeAndFormatIsConvertedWithAWarning()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] original = OriginalLogo(game);
        TextureEntry texture = MainOf(original);
        byte[] dds = MakeDds(Solid(256, 256, 230, 30, 40), TextureFormat.Dxt1, 1);

        Result<byte[]> imported = TextureDds.Import(original, CenterLogo, dds, Codec);

        Assert.True(imported.IsSuccess, imported.Error);
        Assert.NotEmpty(imported.Warnings);
        Assert.Equal(original.Length, imported.Value!.Length);
        TextureEntry result = MainOf(imported.Value);
        Assert.Equal((texture.Format, texture.Width, texture.Height, texture.MipCount), (result.Format, result.Width, result.Height, result.MipCount));

        Rgba32Image decoded = Codec.Decode(result.LinearPixelData, result.Width, result.Height, result.Format).Value!;
        int center = ((decoded.Height / 2 * decoded.Width) + (decoded.Width / 2)) * 4;
        Assert.InRange(decoded.Pixels[center], 220, 240);
    }
}
