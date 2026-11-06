using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class RpsglParsingTests
{
    public static IEnumerable<object[]> AllSampleFiles()
    {
        IReadOnlyList<string> files = SampleFileCatalog.AllSampleRpsglFiles();
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
            return;
        }

        byte[] data = File.ReadAllBytes(path);

        Exception? thrown = Record.Exception(() => RpsglReader.Parse(data, path));

        Assert.Null(thrown);
    }

    [Fact]
    public void CuratedSamples_ThatParseSuccessfully_HaveSaneStructure()
    {
        IReadOnlyList<string> files = SampleFileCatalog.CuratedRpsglFiles();
        if (files.Count == 0)
        {
            return;
        }

        var codec = new DdsCodec();
        int successCount = 0;

        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            Result<RpsglContainer> result = RpsglReader.Parse(data, path);

            // A texlib-record parse failure for a file that actually uses the separate
            // nameplate sub-format (see RpsglReader remarks) is an acceptable, correctly
            // handled outcome for Phase 1, not a bug -- only assert structure for files
            // that *did* parse with at least one texture.
            if (!result.IsSuccess || result.Value!.Textures.Count == 0)
            {
                continue;
            }

            successCount++;

            foreach (TextureEntry tex in result.Value!.Textures)
            {
                Assert.True(tex.Width > 0, $"{path} texture {tex.Index}: width was not positive.");
                Assert.True(tex.Height > 0, $"{path} texture {tex.Index}: height was not positive.");
                Assert.True(tex.MipCount >= 1, $"{path} texture {tex.Index}: mip count was not at least 1.");

                Result<Rgba32Image> decoded = codec.Decode(tex.LinearPixelData, tex.Width, tex.Height, tex.Format);
                Assert.True(decoded.IsSuccess, $"{path} texture {tex.Index}: decode failed: {decoded.Error}");
                Assert.Equal(tex.Width, decoded.Value!.Width);
                Assert.Equal(tex.Height, decoded.Value!.Height);
            }
        }

        Assert.True(successCount > 0, "None of the curated RPSGL samples parsed successfully -- expected at least the texlib-based files to succeed.");
    }
}
