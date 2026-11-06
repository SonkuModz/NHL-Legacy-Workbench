using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class Rx2ParsingTests
{
    public static IEnumerable<object[]> AllSampleFiles()
    {
        IReadOnlyList<string> files = SampleFileCatalog.AllSampleRx2Files();
        if (files.Count == 0)
        {
            yield return new object[] { string.Empty };
            yield break;
        }

        foreach (string file in files)
        {
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Parse_NeverThrows_OverTheFullRealSampleCorpus(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return; // References/ sample tree not present in this environment; nothing to check.
        }

        byte[] data = File.ReadAllBytes(path);

        Exception? thrown = Record.Exception(() => Rx2Reader.Parse(data, path));

        Assert.Null(thrown);
    }

    /// <summary>
    /// The 9 curated "known good" .rx2 files under rx2_modelassets/ turned out (verified
    /// directly against the reference tool) to be mesh-only RX2 files with zero embedded
    /// textures -- RX2-as-texture-container is a real but comparatively rare case,
    /// confirmed present in only 9 of the 1031 files under Extra Model Viewers/ (all
    /// named texlib_*.rx2). So structural/decode sanity is checked across the *full*
    /// sample corpus rather than just the curated subset, since that's where real
    /// texture-bearing RX2 files actually occur; the assertion that at least one file
    /// produced textures is what keeps this test honest about exercising the texture path.
    /// </summary>
    [Fact]
    public void FullCorpus_ParsesSuccessfullyWithSaneStructureWhereTexturesArePresent()
    {
        IReadOnlyList<string> files = SampleFileCatalog.AllSampleRx2Files();
        if (files.Count == 0)
        {
            return;
        }

        var codec = new DdsCodec();
        int filesWithTextures = 0;

        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            Result<Rx2Container> result = Rx2Reader.Parse(data, path);

            Assert.True(result.IsSuccess, $"{path}: {result.Error}");
            Rx2Container container = result.Value!;
            if (container.Textures.Count == 0)
            {
                continue;
            }

            filesWithTextures++;

            foreach (TextureEntry tex in container.Textures)
            {
                Assert.True(tex.Width > 0, $"{path} texture {tex.Index}: width was not positive.");
                Assert.True(tex.Height > 0, $"{path} texture {tex.Index}: height was not positive.");
                Assert.True(tex.MipCount >= 1, $"{path} texture {tex.Index}: mip count was not at least 1.");

                int expectedSize = ExpectedIndependentMipChainSize(tex.Width, tex.Height, tex.Format, tex.MipCount);
                Assert.Equal(expectedSize, tex.LinearPixelData.Length);

                Result<Rgba32Image> decoded = codec.Decode(tex.LinearPixelData, tex.Width, tex.Height, tex.Format);
                Assert.True(decoded.IsSuccess, $"{path} texture {tex.Index}: decode failed: {decoded.Error}");
                Assert.Equal(tex.Width, decoded.Value!.Width);
                Assert.Equal(tex.Height, decoded.Value!.Height);
            }
        }

        Assert.True(filesWithTextures > 0, "No RX2 file in the sample corpus produced any textures -- this test isn't exercising the texture path at all.");
    }

    /// <summary>
    /// Deliberately re-derives the expected mip-chain byte size independently of
    /// <see cref="Rx2MipChainLayout"/> so this is a true cross-check, not the production
    /// formula asserting itself.
    /// </summary>
    private static int ExpectedIndependentMipChainSize(int width, int height, TextureFormat format, int mipCount)
    {
        int bytesPerElement = format switch
        {
            TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked => 8,
            TextureFormat.Dxt3 or TextureFormat.Dxt5 or TextureFormat.Bc5NormalMap => 16,
            TextureFormat.Argb8888 => 4,
            TextureFormat.A1R5G5B5 or TextureFormat.B5G6R5 => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        bool isBlockCompressed = format is TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked
            or TextureFormat.Dxt3 or TextureFormat.Dxt5 or TextureFormat.Bc5NormalMap;

        int total = 0;
        for (int m = 0; m < Math.Max(1, mipCount); m++)
        {
            int mipWidth = Math.Max(1, width >> m);
            int mipHeight = Math.Max(1, height >> m);

            if (isBlockCompressed)
            {
                int blocksWide = Math.Max(1, (mipWidth + 3) / 4);
                int blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
                total += blocksWide * blocksHigh * bytesPerElement;
            }
            else
            {
                total += mipWidth * mipHeight * bytesPerElement;
            }
        }

        return total;
    }
}
