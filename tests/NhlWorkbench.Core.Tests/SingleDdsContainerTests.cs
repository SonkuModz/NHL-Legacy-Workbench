using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class SingleDdsContainerTests
{
    [Fact]
    public void Construct_ExposesExactlyOneTexture()
    {
        var codec = new DdsCodec();
        TextureEntry entry = BuildEntry(codec, out _);

        var container = new SingleDdsContainer(entry, "sample.dds", codec);

        Assert.Equal(ContainerFormat.Dds, container.Format);
        Assert.Equal("sample.dds", container.SourcePath);
        Assert.Single(container.Textures);
        Assert.Same(entry, container.Textures[0]);
    }

    [Fact]
    public void GetPatchCapabilities_OnlySupportsRebuild()
    {
        var codec = new DdsCodec();
        TextureEntry entry = BuildEntry(codec, out _);
        var container = new SingleDdsContainer(entry, "sample.dds", codec);

        PatchCapabilities capabilities = container.GetPatchCapabilities();

        Assert.Equal(PatchCapabilities.Rebuild, capabilities);
        Assert.False(capabilities.HasFlag(PatchCapabilities.InPlace));
    }

    [Fact]
    public void Save_WithInPlaceStrategy_Throws()
    {
        var codec = new DdsCodec();
        TextureEntry entry = BuildEntry(codec, out _);
        var container = new SingleDdsContainer(entry, "sample.dds", codec);

        Assert.Throws<NotSupportedException>(() => container.Save(PatchStrategy.InPlace));
    }

    [Fact]
    public void ReplaceTexture_ThenSave_RoundTripsThroughDdsBytes()
    {
        var codec = new DdsCodec();
        TextureEntry original = BuildEntry(codec, out _);
        var container = new SingleDdsContainer(original, "sample.dds", codec);

        Rgba32Image replacementImage = MakeSolidColor(32, 32, 200);
        Result<byte[]> replacementDds = codec.EncodeToDds(replacementImage, DdsCompressionFormat.Dxt5, DdsConversionOptions.Default);
        Assert.True(replacementDds.IsSuccess, replacementDds.Error);
        Result<DecodedDdsTexture> encoded = codec.ReadDdsFile(replacementDds.Value!);
        Assert.True(encoded.IsSuccess, encoded.Error);

        var replacement = new TextureEntry
        {
            Name = original.Name,
            Index = 0,
            Width = encoded.Value!.Width,
            Height = encoded.Value.Height,
            Format = encoded.Value.Format,
            MipCount = encoded.Value.MipCount,
            LinearPixelData = encoded.Value.LinearData,
        };

        container.ReplaceTexture(0, replacement);
        Assert.Same(replacement, container.Textures[0]);

        byte[] ddsBytes = container.Save(PatchStrategy.Rebuild);

        Result<DecodedDdsTexture> readBack = codec.ReadDdsFile(ddsBytes);
        Assert.True(readBack.IsSuccess, readBack.Error);
        Assert.Equal(replacement.Width, readBack.Value!.Width);
        Assert.Equal(replacement.Height, readBack.Value.Height);
        Assert.Equal(replacement.Format, readBack.Value.Format);
    }

    [Fact]
    public void ReplaceTexture_WithNonZeroIndex_Throws()
    {
        var codec = new DdsCodec();
        TextureEntry entry = BuildEntry(codec, out _);
        var container = new SingleDdsContainer(entry, "sample.dds", codec);

        Assert.Throws<ArgumentOutOfRangeException>(() => container.ReplaceTexture(1, entry));
    }

    private static TextureEntry BuildEntry(DdsCodec codec, out Rgba32Image image)
    {
        image = MakeSolidColor(32, 32, 128);
        Result<byte[]> ddsBytes = codec.EncodeToDds(image, DdsCompressionFormat.Dxt5, DdsConversionOptions.Default);
        Assert.True(ddsBytes.IsSuccess, ddsBytes.Error);

        Result<DecodedDdsTexture> decoded = codec.ReadDdsFile(ddsBytes.Value!);
        Assert.True(decoded.IsSuccess, decoded.Error);

        return new TextureEntry
        {
            Name = "sample",
            Index = 0,
            Width = decoded.Value!.Width,
            Height = decoded.Value.Height,
            Format = decoded.Value.Format,
            MipCount = decoded.Value.MipCount,
            LinearPixelData = decoded.Value.LinearData,
        };
    }

    private static Rgba32Image MakeSolidColor(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }
}
