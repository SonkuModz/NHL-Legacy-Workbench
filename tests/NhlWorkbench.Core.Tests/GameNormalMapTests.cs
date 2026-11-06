using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.JerseyNmStudio;
using NhlWorkbench.Core.JerseySlots;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class GameNormalMapTests
{
    private static Rgba32Image BadMap()
    {
        // A "wrong style" map like the one that lit a jersey lopsidedly: red/green centred low, alpha 118, a little blue.
        var rng = new Random(7);
        var pixels = new byte[256 * 256 * 4];
        for (int i = 0; i < 256 * 256; i++)
        {
            byte bump = (byte)Math.Clamp(84 + (int)((rng.NextDouble() - 0.5) * 40), 0, 255);
            pixels[i * 4] = bump;
            pixels[(i * 4) + 1] = (byte)Math.Clamp(bump + 5, 0, 255);
            pixels[(i * 4) + 2] = 9;
            pixels[(i * 4) + 3] = 118;
        }

        return new Rgba32Image { Width = 256, Height = 256, Pixels = pixels };
    }

    [Fact]
    public void Assess_FlagsAWrongStyleMapAndFixMakesItMatch()
    {
        Rgba32Image bad = BadMap();
        GameNormalMap.Assessment before = GameNormalMap.Assess(bad, TextureFormat.Dxt5);
        Assert.True(before.Applicable);
        Assert.False(before.MatchesGame);
        Assert.Contains("alpha is 118", before.Summary);

        Rgba32Image fixedMap = GameNormalMap.Fix(bad);
        GameNormalMap.Measurement m = GameNormalMap.Measure(fixedMap);
        Assert.Equal(77, m.MinAlpha);
        Assert.Equal(77, m.MaxAlpha);
        Assert.Equal(0, m.MeanBlue);
        Assert.InRange(m.MeanRed, 139.5, 142.5);
        Assert.InRange(m.MeanGreen, 139.0, 142.0);
        Assert.InRange(m.StdRed, 21, 24);
        Assert.True(GameNormalMap.Assess(fixedMap, TextureFormat.Dxt5).MatchesGame);
    }

    [Fact]
    public void Fix_KeepsTheDetailPatternItself()
    {
        Rgba32Image bad = BadMap();
        Rgba32Image fixedMap = GameNormalMap.Fix(bad, matchStrength: false);

        // Without strength matching, every pixel is the source shifted by one constant: neighbours' differences are unchanged.
        for (int i = 1; i < 200; i++)
        {
            int dBad = bad.Pixels[i * 4] - bad.Pixels[(i - 1) * 4];
            int dFixed = fixedMap.Pixels[i * 4] - fixedMap.Pixels[(i - 1) * 4];
            Assert.InRange(Math.Abs(dBad - dFixed), 0, 1);
        }
    }

    [Fact]
    public void Assess_IsNotApplicableToBc5()
    {
        GameNormalMap.Assessment assessment = GameNormalMap.Assess(BadMap(), TextureFormat.Bc5NormalMap);
        Assert.False(assessment.Applicable);
        Assert.True(assessment.MatchesGame);
    }

    [Fact]
    public void RealStockDxt5JerseyNormalMaps_AreRecognisedAsMatchingTheGame_AfterAFullEncodeDecode()
    {
        string jerseyFolder = SampleFileCatalog.JerseyDestinationRoot;
        if (!Directory.Exists(jerseyFolder))
        {
            return;
        }

        var codec = new DdsCodec();
        int checkedCount = 0;
        foreach (string path in new[] { "texlib_1_14_0.rx2", "texlib_1_16_4.rx2", "texlib_1_17_1.rx2", "texlib_1_20_1.rx2", "texlib_1_3_2.rx2" })
        {
            string full = Path.Combine(jerseyFolder, path);
            if (!File.Exists(full))
            {
                continue;
            }

            TextureEntry nm = SlotTextureFile.Load(full).Value!.Textures.First(t => t.Name.StartsWith("jersey_") && t.Name.EndsWith("_nm.Raster"));
            Rgba32Image image = codec.Decode(nm.LinearPixelData, nm.Width, nm.Height, nm.Format).Value!;
            Assert.True(GameNormalMap.Assess(image, nm.Format).MatchesGame, $"{path}: {GameNormalMap.Assess(image, nm.Format).Summary}");

            // A map we fixed and re-compressed to DXT5 still reads as game style.
            Rgba32Image regenerated = GameNormalMap.Fix(image);
            DecodedDdsTexture encoded = codec.EncodeToFormat(regenerated, nm.Format, nm.MipCount).Value!;
            Rgba32Image reread = codec.Decode(encoded.LinearData, encoded.Width, encoded.Height, encoded.Format).Value!;
            Assert.True(GameNormalMap.Assess(reread, nm.Format).MatchesGame, $"{path} after re-encode: {GameNormalMap.Assess(reread, nm.Format).Summary}");
            checkedCount++;
        }

        Assert.True(checkedCount == 0 || checkedCount >= 3);
    }
}
