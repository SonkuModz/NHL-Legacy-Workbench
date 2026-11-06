using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.EquipmentPreview;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class EquipmentPreviewTests
{
    // Nine clearly distinct colors so any zone's coverage can be counted exactly.
    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (250, 10, 10), (10, 250, 10), (10, 10, 250),
        (250, 250, 10), (250, 10, 250), (10, 250, 250),
        (250, 130, 10), (130, 10, 250), (10, 130, 130),
    };

    private static int CountColor(Rgba32Image image, (byte R, byte G, byte B) color)
    {
        int count = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] == color.R && image.Pixels[i + 1] == color.G && image.Pixels[i + 2] == color.B)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void Colorize_AssignsEachMaskChannelToItsZone()
    {
        // 2x1 mask: pixel 0 is pure red (zone 1), pixel 1 is pure blue (zone 3).
        var mask = new Rgba32Image { Width = 2, Height = 1, Pixels = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 } };
        Rgba32Image? result = EquipmentZoneColorizer.Colorize(new[] { mask }, Palette, null);

        Assert.NotNull(result);
        Assert.Equal(new byte[] { 250, 10, 10, 255, 10, 10, 250, 255 }, result!.Pixels);
    }

    [Fact]
    public void Colorize_ReturnsNullWithoutAnyMask()
    {
        Assert.Null(EquipmentZoneColorizer.Colorize(new Rgba32Image?[] { null }, Palette, null));
    }

    [Fact]
    public void RealGloves_LoadAndEveryOneOfTheNineZonesIsPaintable()
    {
        if (!SampleFileCatalog.EquipmentPreviewAvailable)
        {
            return;
        }

        Result<EquipmentPreviewAssets> assets = EquipmentPreviewAssets.Load(SampleFileCatalog.EquipmentPreviewFolder, EquipmentPreviewKind.Gloves);
        Assert.True(assets.IsSuccess, assets.Error);
        Assert.True(assets.Value!.Mesh.Vertices.Length > 500);
        Assert.Equal(3, assets.Value.ZoneMasks.Count);
        Assert.All(assets.Value.ZoneMasks, m => Assert.NotNull(m));

        Rgba32Image colored = EquipmentZoneColorizer.Colorize(assets.Value.ZoneMasks, Palette, assets.Value.Detail)!;
        for (int zone = 0; zone < 9; zone++)
        {
            Assert.True(CountColor(colored, Palette[zone]) > 0, $"Glove zone {zone + 1} never appears in the colorized texture.");
        }
    }

    [Fact]
    public void RealHelmet_LoadsAndItsThreeZonesArePaintable()
    {
        if (!SampleFileCatalog.EquipmentPreviewAvailable)
        {
            return;
        }

        Result<EquipmentPreviewAssets> assets = EquipmentPreviewAssets.Load(SampleFileCatalog.EquipmentPreviewFolder, EquipmentPreviewKind.Helmet);
        Assert.True(assets.IsSuccess, assets.Error);
        Assert.True(assets.Value!.Mesh.Vertices.Length > 1000);
        Assert.NotNull(assets.Value.Detail);

        // Without the detail overlay, the three mask channels must map to zones 1-3 only.
        Rgba32Image plain = EquipmentZoneColorizer.Colorize(assets.Value.ZoneMasks, Palette, null)!;
        for (int zone = 0; zone < 3; zone++)
        {
            Assert.True(CountColor(plain, Palette[zone]) > 0, $"Helmet zone {zone + 1} never appears in the colorized texture.");
        }

        for (int zone = 3; zone < 9; zone++)
        {
            Assert.Equal(0, CountColor(plain, Palette[zone]));
        }
    }

    [Fact]
    public void Load_MissingFolderFailsCleanly()
    {
        Assert.False(EquipmentPreviewAssets.Load(Path.Combine(Path.GetTempPath(), "no-such-equipment-folder"), EquipmentPreviewKind.Gloves).IsSuccess);
    }
}
