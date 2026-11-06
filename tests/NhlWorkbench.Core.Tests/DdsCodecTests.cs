using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class DdsCodecTests
{
    [Theory]
    [InlineData(DdsCompressionFormat.Dxt1)]
    [InlineData(DdsCompressionFormat.Dxt2)]
    [InlineData(DdsCompressionFormat.Dxt3)]
    [InlineData(DdsCompressionFormat.Dxt4)]
    [InlineData(DdsCompressionFormat.Dxt5)]
    [InlineData(DdsCompressionFormat.Bc7)]
    public void EncodeToDds_ProducesAValidDdsFile(DdsCompressionFormat format)
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(64, 64);

        Result<byte[]> result = codec.EncodeToDds(image, format, DdsConversionOptions.Default);

        Assert.True(result.IsSuccess, result.Error);
        byte[] dds = result.Value!;
        Assert.True(dds.Length > 128, "Encoded DDS is implausibly small.");
        Assert.Equal((byte)'D', dds[0]);
        Assert.Equal((byte)'D', dds[1]);
        Assert.Equal((byte)'S', dds[2]);
        Assert.Equal((byte)' ', dds[3]);
    }

    [Fact]
    public void EncodeToDds_WithMipmapsAndResize_Succeeds()
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(128, 128);
        var options = new DdsConversionOptions { GenerateMipmaps = true, ResizeTo = (64, 64) };

        Result<byte[]> result = codec.EncodeToDds(image, DdsCompressionFormat.Dxt5, options);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.Value!.Length > 128);
    }

    [Fact]
    public void EncodeToDds_WithPreserveAlphaFalse_StillSucceeds()
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(32, 32);
        var options = new DdsConversionOptions { PreserveAlpha = false };

        Result<byte[]> result = codec.EncodeToDds(image, DdsCompressionFormat.Dxt5, options);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public void AdjustMipCount_Shrinking_KeepsOnlyRequestedLevelsAndPreservesMip0()
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(64, 64);
        var options = new DdsConversionOptions { GenerateMipmaps = true };
        byte[] dds = codec.EncodeToDds(image, DdsCompressionFormat.Dxt1, options).Value!;
        DecodedDdsTexture source = codec.ReadDdsFile(dds).Value!;
        Assert.True(source.MipCount > 1, "Expected the encoded DDS to have multiple mip levels.");

        Result<DecodedDdsTexture> result = codec.AdjustMipCount(source, 1);

        Assert.True(result.IsSuccess, result.Error);
        DecodedDdsTexture adjusted = result.Value!;
        Assert.Equal(1, adjusted.MipCount);
        Assert.Equal(64, adjusted.Width);
        Assert.Equal(64, adjusted.Height);

        int mip0Size = 16 * 16 * 8; // 64x64 DXT1: 16x16 blocks * 8 bytes/block
        Assert.Equal(mip0Size, adjusted.LinearData.Length);
        Assert.Equal(source.LinearData[..mip0Size], adjusted.LinearData);
    }

    [Fact]
    public void AdjustMipCount_Growing_ProducesRequestedLevelCountAndPreservesMip0()
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(64, 64);
        byte[] dds = codec.EncodeToDds(image, DdsCompressionFormat.Dxt1, DdsConversionOptions.Default).Value!;
        DecodedDdsTexture source = codec.ReadDdsFile(dds).Value!;
        Assert.Equal(1, source.MipCount);

        Result<DecodedDdsTexture> result = codec.AdjustMipCount(source, 4);

        Assert.True(result.IsSuccess, result.Error);
        DecodedDdsTexture adjusted = result.Value!;
        Assert.Equal(4, adjusted.MipCount);

        int expectedTotal = 0;
        for (int m = 0; m < 4; m++)
        {
            int mipSize = Math.Max(1, 64 >> m);
            int blocks = Math.Max(1, (mipSize + 3) / 4);
            expectedTotal += blocks * blocks * 8;
        }

        Assert.Equal(expectedTotal, adjusted.LinearData.Length);

        int mip0Size = 16 * 16 * 8;
        Assert.Equal(source.LinearData[..mip0Size], adjusted.LinearData[..mip0Size]);
    }

    [Fact]
    public void AdjustMipCount_SameCount_ReturnsSourceUnchanged()
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(32, 32);
        byte[] dds = codec.EncodeToDds(image, DdsCompressionFormat.Dxt1, DdsConversionOptions.Default).Value!;
        DecodedDdsTexture source = codec.ReadDdsFile(dds).Value!;

        Result<DecodedDdsTexture> result = codec.AdjustMipCount(source, source.MipCount);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Same(source, result.Value);
    }

    /// <summary>
    /// Regression test for a real corruption bug found via a user's actual batch RPSGL-to-DDS
    /// export (Batch Tools' <c>Parallel.ForEach</c> calling a single shared <c>DdsCodec</c>
    /// instance's methods concurrently): DirectXTexNet's <c>TexHelper.Instance</c> is a
    /// process-wide singleton, so concurrent native calls into it from multiple threads can
    /// scramble/cross-contaminate each other's pixel data -- producing a texture that looks like
    /// its content was "shifted" to a different position, when actually it's a race in the
    /// native encode/decode path, not a positional bug. Each thread repeatedly round-trips its
    /// own solid, distinguishable color and must always get exactly that color back.
    /// </summary>
    [Fact]
    public void EncodeThenDecode_CalledConcurrentlyFromManyThreads_NeverCrossContaminatesPixelData()
    {
        var codec = new DdsCodec();
        int threadCount = Math.Max(4, Environment.ProcessorCount);
        const int iterationsPerThread = 15;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = threadCount };
        Parallel.For(0, threadCount, parallelOptions, threadIndex =>
        {
            byte colorValue = (byte)(threadIndex * 30 + 10);
            for (int i = 0; i < iterationsPerThread; i++)
            {
                // 512x512 (real pant-texture scale, not a tiny 32x32) with real mip generation,
                // mirroring exactly what BatchToolsViewModel/TextureEditorViewModel call in
                // production -- WrapAsDds via a *shared* DdsCodec instance from a Parallel.ForEach.
                Rgba32Image solid = MakeSolidColor(512, 512, colorValue);
                Result<byte[]> encodedDds = codec.EncodeToDds(solid, DdsCompressionFormat.Dxt1, new DdsConversionOptions { GenerateMipmaps = true });
                if (!encodedDds.IsSuccess)
                {
                    failures.Add($"thread {threadIndex} iter {i}: encode failed: {encodedDds.Error}");
                    continue;
                }

                DecodedDdsTexture roundTripped = codec.ReadDdsFile(encodedDds.Value!).Value!;
                Result<byte[]> wrapped = codec.WrapAsDds(roundTripped.LinearData, roundTripped.Width, roundTripped.Height, roundTripped.Format, roundTripped.MipCount);
                if (!wrapped.IsSuccess)
                {
                    failures.Add($"thread {threadIndex} iter {i}: wrap failed: {wrapped.Error}");
                    continue;
                }

                Result<DecodedDdsTexture> read = codec.ReadDdsFile(wrapped.Value!);
                if (!read.IsSuccess)
                {
                    failures.Add($"thread {threadIndex} iter {i}: read failed: {read.Error}");
                    continue;
                }

                Result<Rgba32Image> decoded = codec.Decode(read.Value!.LinearData, read.Value.Width, read.Value.Height, read.Value.Format);
                if (!decoded.IsSuccess)
                {
                    failures.Add($"thread {threadIndex} iter {i}: decode failed: {decoded.Error}");
                    continue;
                }

                byte[] pixels = decoded.Value!.Pixels;
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    // DXT1 is lossy, so allow a small tolerance -- but cross-thread contamination
                    // shows up as a wildly different value (a different thread's color band),
                    // not a small compression rounding error.
                    if (Math.Abs(pixels[p] - colorValue) > 10)
                    {
                        failures.Add($"thread {threadIndex} iter {i}: pixel {p} = {pixels[p]}, expected ~{colorValue}");
                        break;
                    }
                }
            }
        });

        Assert.True(failures.IsEmpty, $"{failures.Count} contamination failure(s):\n{string.Join("\n", failures.Take(10))}");
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

    [Theory]
    [InlineData(TextureFormat.Bc5NormalMap)]
    [InlineData(TextureFormat.Dxt5)]
    public void EncodeToFormat_RoundTrips_WithFullMipChainAndCorrectDimensions(TextureFormat format)
    {
        var codec = new DdsCodec();
        Rgba32Image image = MakeCheckerboard(64, 64);

        Result<DecodedDdsTexture> encoded = codec.EncodeToFormat(image, format, mipCount: 7);

        Assert.True(encoded.IsSuccess, encoded.Error);
        DecodedDdsTexture texture = encoded.Value!;
        Assert.Equal(64, texture.Width);
        Assert.Equal(64, texture.Height);
        Assert.Equal(7, texture.MipCount);
        Assert.Equal(format, texture.Format);

        // The encoded chain must actually decode back (i.e. isn't just the right length by
        // coincidence) -- this is what would have caught the pant "missing mip" class of bug.
        Result<Rgba32Image> decoded = codec.Decode(texture.LinearData, texture.Width, texture.Height, texture.Format);
        Assert.True(decoded.IsSuccess, decoded.Error);
        Assert.Equal(64, decoded.Value!.Width);
        Assert.Equal(64, decoded.Value.Height);
    }

    private static Rgba32Image MakeCheckerboard(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 4;
                bool light = ((x / 4) + (y / 4)) % 2 == 0;
                byte value = (byte)(light ? 220 : 30);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        return new Rgba32Image { Width = width, Height = height, Pixels = pixels };
    }
}
