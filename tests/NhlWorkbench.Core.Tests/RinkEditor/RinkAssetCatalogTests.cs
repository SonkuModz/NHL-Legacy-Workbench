using NhlWorkbench.Core.RinkEditor;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests.RinkEditor;

public class RinkAssetCatalogTests
{
    private static string CreateFile(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void Enumerate_ParsesCenterLogoAndHotspotFileNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "RinkAssetCatalogTests_" + Guid.NewGuid());
        try
        {
            CreateFile(root, "centerlogo_24_cm.rx2");
            CreateFile(root, "ice_hotspot_0_2_cm.rx2");
            CreateFile(root, "ice_hotspot_1_15_cm.rx2");
            CreateFile(root, "not_a_match.rx2");
            CreateFile(root, "readme.txt");

            IReadOnlyList<RinkAssetCatalogEntry> entries = RinkAssetCatalog.Enumerate(root);

            Assert.Equal(3, entries.Count);

            RinkAssetCatalogEntry logo = Assert.Single(entries, e => e.Kind == RinkAssetKind.CenterLogo);
            Assert.Equal(24, logo.Id);
            Assert.Null(logo.HotspotVariant);
            Assert.EndsWith("centerlogo_24_cm.rx2", logo.FullPath);

            RinkAssetCatalogEntry hotspotVariant0 = Assert.Single(entries, e => e.Kind == RinkAssetKind.Hotspot && e.Id == 2);
            Assert.Equal(0, hotspotVariant0.HotspotVariant);

            RinkAssetCatalogEntry hotspotVariant1 = Assert.Single(entries, e => e.Kind == RinkAssetKind.Hotspot && e.Id == 15);
            Assert.Equal(1, hotspotVariant1.HotspotVariant);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_NonExistentDirectory_ReturnsEmptyWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "RinkAssetCatalogTests_missing_" + Guid.NewGuid());

        IReadOnlyList<RinkAssetCatalogEntry> entries = RinkAssetCatalog.Enumerate(missing);

        Assert.Empty(entries);
    }

    [Fact]
    public void Enumerate_RealIceSurfaceTree_FindsExpectedCountsAndDefaultHotspotIds()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<RinkAssetCatalogEntry> entries = RinkAssetCatalog.Enumerate(SampleFileCatalog.ArenaIceSurfaceRoot);

        int centerLogoCount = entries.Count(e => e.Kind == RinkAssetKind.CenterLogo);
        int hotspotVariant0Count = entries.Count(e => e.Kind == RinkAssetKind.Hotspot && e.HotspotVariant == 0);

        Assert.True(centerLogoCount >= 200, $"Expected at least 200 center logos, found {centerLogoCount}.");
        Assert.True(hotspotVariant0Count >= 70, $"Expected at least 70 variant-0 hotspots, found {hotspotVariant0Count}.");

        int[] defaultHotspotIds = { 2, 48, 55, 72 };
        foreach (int id in defaultHotspotIds)
        {
            Assert.Contains(entries, e => e.Kind == RinkAssetKind.Hotspot && e.HotspotVariant == 0 && e.Id == id);
        }
    }
}
