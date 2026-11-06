using NhlWorkbench.Core.Assets;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class ArenaAssetLinkerTests
{
    private static string CreateFile(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void Link_ExtractsArenaIdAndLinksAllThreeAssetKinds()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArenaAssetLinkerTests_" + Guid.NewGuid());
        string stadiumDir = Path.Combine(root, "stadium");
        string logoDir = Path.Combine(root, "icesurface");
        string jumbotronDir = Path.Combine(root, "jumbotron");
        try
        {
            CreateFile(stadiumDir, "stadium_24_container_0.rx2");
            CreateFile(logoDir, "centerlogo_24_cm.rx2");
            CreateFile(jumbotronDir, "texlib_24.rx2");

            ArenaAssetLinkResult result = ArenaAssetLinker.Link(stadiumDir, logoDir, jumbotronDir);

            ArenaAssetGroup arena = Assert.Single(result.Arenas);
            Assert.Equal(24, arena.ArenaId);
            Assert.EndsWith("stadium_24_container_0.rx2", arena.StadiumContainerPath);
            Assert.NotNull(arena.CenterLogoPath);
            Assert.EndsWith("centerlogo_24_cm.rx2", arena.CenterLogoPath);
            Assert.NotNull(arena.JumbotronPath);
            Assert.EndsWith("texlib_24.rx2", arena.JumbotronPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Link_MissingLogoOrJumbotron_LeavesThemNullWithoutError()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArenaAssetLinkerTests_" + Guid.NewGuid());
        string stadiumDir = Path.Combine(root, "stadium");
        string logoDir = Path.Combine(root, "icesurface");
        string jumbotronDir = Path.Combine(root, "jumbotron");
        try
        {
            CreateFile(stadiumDir, "stadium_7_container_0.rx2");
            Directory.CreateDirectory(logoDir);
            Directory.CreateDirectory(jumbotronDir);

            ArenaAssetLinkResult result = ArenaAssetLinker.Link(stadiumDir, logoDir, jumbotronDir);

            ArenaAssetGroup arena = Assert.Single(result.Arenas);
            Assert.Equal(7, arena.ArenaId);
            Assert.Null(arena.CenterLogoPath);
            Assert.Null(arena.JumbotronPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Link_NonExistentDirectories_ReturnsEmptyWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ArenaAssetLinkerTests_missing_" + Guid.NewGuid());

        ArenaAssetLinkResult result = ArenaAssetLinker.Link(missing, missing, missing);

        Assert.Empty(result.Arenas);
        Assert.Empty(result.SharedIceSurfaces);
    }

    [Fact]
    public void Link_SharedIceSurfaceFiles_AreSurfacedSeparatelyNotAsArenas()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArenaAssetLinkerTests_" + Guid.NewGuid());
        string stadiumDir = Path.Combine(root, "stadium");
        try
        {
            CreateFile(stadiumDir, "ice_nhl_container_0.rx2");
            CreateFile(stadiumDir, "ice_intl_container_0.rx2");
            CreateFile(stadiumDir, "ice_nhl_full_lm_container_0.rx2");
            CreateFile(stadiumDir, "stadium_1_container_0.rx2");

            ArenaAssetLinkResult result = ArenaAssetLinker.Link(stadiumDir, stadiumDir, stadiumDir);

            Assert.Equal(3, result.SharedIceSurfaces.Count);
            ArenaAssetGroup arena = Assert.Single(result.Arenas);
            Assert.Equal(1, arena.ArenaId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Link_RealArenaTree_LinksArena24ToAllThreeRealFiles()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return;
        }

        ArenaAssetLinkResult result = ArenaAssetLinker.Link(
            SampleFileCatalog.ArenaStadiumRoot,
            SampleFileCatalog.ArenaIceSurfaceRoot,
            SampleFileCatalog.ArenaJumbotronRoot);

        ArenaAssetGroup? arena24 = result.Arenas.FirstOrDefault(a => a.ArenaId == 24);
        Assert.NotNull(arena24);
        Assert.EndsWith("stadium_24_container_0.rx2", arena24!.StadiumContainerPath);
        Assert.NotNull(arena24.CenterLogoPath);
        Assert.EndsWith("centerlogo_24_cm.rx2", arena24.CenterLogoPath);
        Assert.NotNull(arena24.JumbotronPath);
        Assert.EndsWith("texlib_24.rx2", arena24.JumbotronPath);

        Assert.Equal(3, result.SharedIceSurfaces.Count);
    }
}
