using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.JerseySlots;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class Rx2NameRewriterTests
{
    private static string Compiled => Path.GetFullPath(Path.Combine(SampleFileCatalog.JerseyDestinationRoot, "..", ".."));

    [Fact]
    public void Rewrite_RefusesDifferentLengthAndMissingToken()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("\0jersey_1_0_2_cm.Raster\0");
        Assert.False(Rx2NameRewriter.Rewrite(data, "_1_0_2", "_1_0_12").IsSuccess);
        Assert.False(Rx2NameRewriter.Rewrite(data, "_1_9_9", "_1_9_8").IsSuccess);
    }

    [Fact]
    public void Rewrite_OnlyTouchesWholeNamesNotLongerNumbers()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("\0jersey_1_0_2_cm.Raster\0jersey_1_0_25_cm.Raster\0");
        Result<byte[]> result = Rx2NameRewriter.Rewrite(data, "_1_0_2", "_1_0_5");
        Assert.True(result.IsSuccess);
        string text = System.Text.Encoding.ASCII.GetString(result.Value!);
        Assert.Contains("jersey_1_0_5_cm.Raster", text);
        Assert.Contains("jersey_1_0_25_cm.Raster", text); // a longer number must be left alone
    }

    [Fact]
    public void RealSlotFiles_AreRetargetedByteMinimallyAndRoundTrip()
    {
        string jersey = Path.Combine(Compiled, "rendering", "jersey");
        if (!Directory.Exists(jersey) || !File.Exists(Path.Combine(jersey, "texlib_1_0_2.rx2")))
        {
            return;
        }

        foreach ((string folder, string name) in new[]
        {
            (jersey, JerseySlotEditor.TexlibFileName(1, 0, 2)),
            (jersey, JerseySlotEditor.NameplateFileName(1, 0, 2)),
            (Path.Combine(Compiled, "rendering", "pant"), JerseySlotEditor.PantFileName(1, 0, 2)),
            (Path.Combine(Compiled, "rendering", "sock"), JerseySlotEditor.SockFileName(1, 0, 2)),
        })
        {
            string path = Path.Combine(folder, name);
            byte[] original = File.ReadAllBytes(path);
            Result<byte[]> retargeted = Rx2NameRewriter.Rewrite(original, JerseySlotEditor.NameToken(1, 0, 2), JerseySlotEditor.NameToken(1, 0, 5));
            Assert.True(retargeted.IsSuccess, $"{name}: {retargeted.Error}");

            byte[] changed = retargeted.Value!;
            Assert.Equal(original.Length, changed.Length);
            int differing = original.Where((b, i) => b != changed[i]).Count();
            Assert.InRange(differing, 1, 40); // only the digits inside name strings

            // Every texture name now carries the new variant and none the old.
            Result<Rx2Container> parsed = Rx2Reader.Parse(changed, path);
            Assert.True(parsed.IsSuccess, parsed.Error);
            Assert.All(parsed.Value!.Textures, t =>
            {
                Assert.Contains("_1_0_5", t.Name);
                Assert.DoesNotContain("_1_0_2", t.Name);
            });

            // Pixel data identical to the original's.
            Result<Rx2Container> before = Rx2Reader.Parse(original, path);
            for (int i = 0; i < before.Value!.Textures.Count; i++)
            {
                Assert.Equal(before.Value.Textures[i].LinearPixelData, parsed.Value.Textures[i].LinearPixelData);
            }

            // And the rewrite is exactly reversible.
            Assert.Equal(original, Rx2NameRewriter.Rewrite(changed, JerseySlotEditor.NameToken(1, 0, 5), JerseySlotEditor.NameToken(1, 0, 2)).Value);
        }
    }
}
