using System.Linq;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Regression coverage for a real, confirmed data-corruption bug: stadium/arena RX2
/// containers embed BOTH texture data AND 3D mesh geometry (vertex/index buffers) in the
/// same GPU-data region -- a combination never exercised by the original RX2 texture
/// reader/writer, which was ground-truth-verified only against pure texture containers
/// (jerseys/players/texlibs). The original <c>Rx2Writer.Rebuild</c> repacked only the
/// known textures from scratch and silently discarded everything else sharing that
/// region, corrupting the embedded mesh (same vertex *count*, garbage vertex *positions*)
/// whenever a replacement texture's dimensions/format/mip-count didn't exactly match the
/// original -- which a user hit while modding a stadium arena and saw "everything
/// invisible" in-game. Fixed by making Rebuild non-destructive: it now only touches the
/// specific texture(s) actually being replaced (in place if the new data fits the
/// original slot, appended past the end of the file otherwise), leaving every other byte
/// -- including the embedded mesh -- untouched.
/// </summary>
public class Rx2StadiumRebuildTests
{
    private static string? FindRealStadiumFile()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return null;
        }

        return System.IO.Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "stadium_*_container_0.rx2").FirstOrDefault();
    }

    [Fact]
    public void Rebuild_ResizedTextureThatFitsOriginalSlot_PreservesOtherTexturesAndMesh()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        byte[] original = System.IO.File.ReadAllBytes(path);
        Rx2Container container = Rx2Reader.Parse(original, path).Value!;
        Rx2Model meshBefore = Rx2ModelReader.Parse(original, path).Value!;

        TextureEntry original0 = container.Textures[0];
        var resized = new TextureEntry
        {
            Name = original0.Name,
            Index = original0.Index,
            Width = original0.Width * 2,
            Height = original0.Height,
            Format = original0.Format,
            MipCount = 1,
            LinearPixelData = new byte[EstimateBc1n3LinearSize(original0.Width * 2, original0.Height, original0.Format)],
        };

        container.ReplaceTexture(0, resized);
        Assert.Equal(PatchCapabilities.Rebuild, container.GetPatchCapabilities());

        byte[] rebuilt = container.Save(PatchStrategy.Rebuild);
        Rx2Container reparsed = Rx2Reader.Parse(rebuilt, path).Value!;

        Assert.Equal(resized.Width, reparsed.Textures[0].Width);

        for (int i = 1; i < container.Textures.Count; i++)
        {
            Assert.Equal(container.Textures[i].LinearPixelData, reparsed.Textures[i].LinearPixelData);
        }

        AssertMeshUnchanged(meshBefore, Rx2ModelReader.Parse(rebuilt, path).Value!);
    }

    [Fact]
    public void Rebuild_ResizedTextureThatDoesNotFitOriginalSlot_AppendsWithoutDisturbingOthers()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        byte[] original = System.IO.File.ReadAllBytes(path);
        Rx2Container container = Rx2Reader.Parse(original, path).Value!;
        Rx2Model meshBefore = Rx2ModelReader.Parse(original, path).Value!;

        TextureEntry original0 = container.Textures[0];
        var muchBigger = new TextureEntry
        {
            Name = original0.Name,
            Index = original0.Index,
            Width = original0.Width * 8,
            Height = original0.Height * 8,
            Format = original0.Format,
            MipCount = 1,
            LinearPixelData = new byte[EstimateBc1n3LinearSize(original0.Width * 8, original0.Height * 8, original0.Format)],
        };

        container.ReplaceTexture(0, muchBigger);
        byte[] rebuilt = container.Save(PatchStrategy.Rebuild);

        Assert.True(rebuilt.Length > original.Length, "Expected the file to grow when a replacement can't fit its original slot.");

        Rx2Container reparsed = Rx2Reader.Parse(rebuilt, path).Value!;
        Assert.Equal(muchBigger.Width, reparsed.Textures[0].Width);

        for (int i = 1; i < container.Textures.Count; i++)
        {
            Assert.Equal(container.Textures[i].LinearPixelData, reparsed.Textures[i].LinearPixelData);
        }

        AssertMeshUnchanged(meshBefore, Rx2ModelReader.Parse(rebuilt, path).Value!);
    }

    [Fact]
    public void Rebuild_WithNoStagedChanges_IsByteEquivalentToOriginalForMeshAndTextures()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        byte[] original = System.IO.File.ReadAllBytes(path);
        Rx2Container container = Rx2Reader.Parse(original, path).Value!;
        Rx2Model meshBefore = Rx2ModelReader.Parse(original, path).Value!;

        byte[] rebuilt = container.Save(PatchStrategy.Rebuild);
        Rx2Container reparsed = Rx2Reader.Parse(rebuilt, path).Value!;

        for (int i = 0; i < container.Textures.Count; i++)
        {
            Assert.Equal(container.Textures[i].LinearPixelData, reparsed.Textures[i].LinearPixelData);
        }

        AssertMeshUnchanged(meshBefore, Rx2ModelReader.Parse(rebuilt, path).Value!);
    }

    private static void AssertMeshUnchanged(Rx2Model before, Rx2Model after)
    {
        Assert.Equal(before.Meshes.Count, after.Meshes.Count);
        for (int i = 0; i < before.Meshes.Count; i++)
        {
            Rx2Mesh a = before.Meshes[i];
            Rx2Mesh b = after.Meshes[i];
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            for (int v = 0; v < a.Vertices.Length; v++)
            {
                Assert.Equal(a.Vertices[v].Position, b.Vertices[v].Position);
            }
        }
    }

    private static int EstimateBc1n3LinearSize(int width, int height, TextureFormat format)
    {
        int blocksW = System.Math.Max(1, (width + 3) / 4);
        int blocksH = System.Math.Max(1, (height + 3) / 4);
        int bytesPerBlock = format is TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked ? 8 : 16;
        return blocksW * blocksH * bytesPerBlock;
    }
}
