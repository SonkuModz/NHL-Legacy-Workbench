using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// The concrete acceptance test for "preserve original container structure whenever
/// possible": feed each real sample container's own unchanged decoded pixel data back
/// through <see cref="PatchStrategy.InPlace"/> and assert the output is byte-for-byte
/// identical to the original file. This is the strongest signal that the tiling/untiling
/// round trip and offset bookkeeping are correct -- a naive "always rebuild" writer would
/// never be held to this bar.
/// </summary>
public class RoundtripPatchTests
{
    /// <summary>
    /// RX2-as-texture-container is a real but comparatively rare case in the sample
    /// corpus (only 9 of 1031 .rx2 files under Extra Model Viewers/ actually embed
    /// textures -- the curated rx2_modelassets/ set turned out to be mesh-only files with
    /// zero textures, verified against the reference tool). Scanning the full corpus
    /// here, rather than just the curated subset, is what makes this test exercise the
    /// tiling/untiling round trip at all instead of trivially "passing" on files with
    /// nothing to patch.
    /// </summary>
    [Fact]
    public void Rx2_InPlacePatch_WithUnchangedPixelData_IsByteIdenticalToOriginal()
    {
        IReadOnlyList<string> files = SampleFileCatalog.AllSampleRx2Files();
        if (files.Count == 0)
        {
            return;
        }

        int filesWithTextures = 0;
        foreach (string path in files)
        {
            byte[] original = File.ReadAllBytes(path);
            Result<Rx2Container> parsed = Rx2Reader.Parse(original, path);
            if (!parsed.IsSuccess || parsed.Value!.Textures.Count == 0)
            {
                continue;
            }

            Rx2Container container = parsed.Value!;
            foreach (TextureEntry tex in container.Textures)
            {
                container.ReplaceTexture(tex.Index, CloneAsReplacement(tex));
            }

            byte[] patched = container.Save(PatchStrategy.InPlace);
            Assert.True(original.AsSpan().SequenceEqual(patched), $"InPlace patch was not byte-identical for {path}");
            filesWithTextures++;
        }

        Assert.True(filesWithTextures > 0, "No RX2 sample with embedded textures was found -- this test isn't exercising the patch path at all.");
    }

    [Fact]
    public void Rpsgl_InPlacePatch_WithUnchangedPixelData_IsByteIdenticalToOriginal()
    {
        IReadOnlyList<string> files = SampleFileCatalog.CuratedRpsglFiles();
        if (files.Count == 0)
        {
            return;
        }

        int checkedCount = 0;
        foreach (string path in files)
        {
            byte[] original = File.ReadAllBytes(path);
            Result<RpsglContainer> parsed = RpsglReader.Parse(original, path);
            if (!parsed.IsSuccess || parsed.Value!.Textures.Count == 0)
            {
                continue;
            }

            RpsglContainer container = parsed.Value!;
            foreach (TextureEntry tex in container.Textures)
            {
                container.ReplaceTexture(tex.Index, CloneAsReplacement(tex));
            }

            byte[] patched = container.Save(PatchStrategy.InPlace);
            Assert.True(original.AsSpan().SequenceEqual(patched), $"InPlace patch was not byte-identical for {path}");
            checkedCount++;
        }

        Assert.True(checkedCount > 0, "No curated RPSGL samples parsed via the texlib path -- expected at least some to succeed.");
    }

    private static TextureEntry CloneAsReplacement(TextureEntry original) => new()
    {
        Name = original.Name,
        Index = original.Index,
        Width = original.Width,
        Height = original.Height,
        Format = original.Format,
        MipCount = original.MipCount,
        LinearPixelData = original.LinearPixelData,
    };
}
