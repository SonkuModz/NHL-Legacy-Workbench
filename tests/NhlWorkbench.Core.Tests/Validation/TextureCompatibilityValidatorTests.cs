using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Validation;
using Xunit;

namespace NhlWorkbench.Core.Tests.Validation;

public class TextureCompatibilityValidatorTests
{
    private static TextureEntry MakeOriginal(int width = 1024, int height = 1024, TextureFormat format = TextureFormat.Dxt5, int mipCount = 11)
        => new() { Name = "original", Width = width, Height = height, Format = format, MipCount = mipCount };

    private static DecodedDdsTexture MakeReplacement(int width = 1024, int height = 1024, TextureFormat format = TextureFormat.Dxt5, int mipCount = 11)
        => new(format, width, height, mipCount, Array.Empty<byte>());

    [Fact]
    public void AreCompatible_IdenticalShape_ReturnsTrue()
    {
        Assert.True(TextureCompatibilityValidator.AreCompatible(MakeOriginal(), MakeReplacement()));
        Assert.Null(TextureCompatibilityValidator.DescribeMismatch(MakeOriginal(), MakeReplacement()));
    }

    [Fact]
    public void AreCompatible_DifferentDimensions_ReturnsFalseAndDescribesMismatch()
    {
        TextureEntry original = MakeOriginal();
        DecodedDdsTexture replacement = MakeReplacement(width: 2048, height: 2048);

        Assert.False(TextureCompatibilityValidator.AreCompatible(original, replacement));
        string? description = TextureCompatibilityValidator.DescribeMismatch(original, replacement);
        Assert.NotNull(description);
        Assert.Contains("dimensions", description);
    }

    [Fact]
    public void AreCompatible_DifferentFormat_ReturnsFalseAndDescribesMismatch()
    {
        TextureEntry original = MakeOriginal();
        DecodedDdsTexture replacement = MakeReplacement(format: TextureFormat.Dxt1);

        Assert.False(TextureCompatibilityValidator.AreCompatible(original, replacement));
        string? description = TextureCompatibilityValidator.DescribeMismatch(original, replacement);
        Assert.NotNull(description);
        Assert.Contains("format", description);
    }

    [Fact]
    public void AreCompatible_DifferentMipCount_ReturnsFalseAndDescribesMismatch()
    {
        TextureEntry original = MakeOriginal(mipCount: 1);
        DecodedDdsTexture replacement = MakeReplacement(mipCount: 11);

        Assert.False(TextureCompatibilityValidator.AreCompatible(original, replacement));
        string? description = TextureCompatibilityValidator.DescribeMismatch(original, replacement);
        Assert.NotNull(description);
        Assert.Contains("mip count", description);
    }
}
