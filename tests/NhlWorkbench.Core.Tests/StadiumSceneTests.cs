using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.StadiumEditor;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Ground-truths <see cref="StadiumScene"/> (the Stadium Editor's texture-to-submesh
/// auto-assignment) against a real, unedited stadium container.</summary>
public class StadiumSceneTests
{
    [Fact]
    public void Load_RealStadiumContainer_NeverThrowsAndResolvesAtLeastSomeMaterials()
    {
        if (!SampleFileCatalog.StadiumContainerSampleAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.StadiumContainerSampleFile);
        Result<StadiumScene> result = StadiumScene.Load(data, SampleFileCatalog.StadiumContainerSampleFile);

        Assert.True(result.IsSuccess, result.Error);
        StadiumScene scene = result.Value!;

        Assert.NotEmpty(scene.Submeshes);

        // A missing material/texture is normal for some submeshes (see Rx2MaterialReader's own
        // contract) -- but a combined stadium container should resolve at least *some* of them,
        // and every resolved texture must have actually decoded to real pixels.
        Assert.Contains(scene.Submeshes, s => s.Material is not null);
        Assert.Contains(scene.Submeshes, s => s.DiffuseMap is not null);

        foreach (var submesh in scene.Submeshes)
        {
            if (submesh.DiffuseMap is not null)
            {
                Assert.True(submesh.DiffuseMap.Width > 0 && submesh.DiffuseMap.Height > 0);
                Assert.Equal(submesh.DiffuseMap.Width * submesh.DiffuseMap.Height * 4, submesh.DiffuseMap.Pixels.Length);
            }
        }
    }

    [Fact]
    public void Load_RealStadiumFolder_NeverThrowsOnAnyFile()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "stadium_*_container_0.rx2"))
        {
            byte[] data = File.ReadAllBytes(file);
            Result<StadiumScene> result = StadiumScene.Load(data, file);

            Assert.True(result.IsSuccess, $"'{file}': {result.Error}");
        }
    }
}
