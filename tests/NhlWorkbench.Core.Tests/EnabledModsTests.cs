using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class EnabledModsTests : IDisposable
{
    private const string Logo = "rendering/icesurface/centerlogo_0_cm.rpsgl";

    private readonly string _install = Path.Combine(Path.GetTempPath(), "NhlEnabledMods_" + Guid.NewGuid().ToString("N"));

    public EnabledModsTests() => WriteFile("game/PS3_GAME/USRDIR/" + Logo, [1, 2, 3]);

    public void Dispose()
    {
        if (Directory.Exists(_install))
        {
            Directory.Delete(_install, recursive: true);
        }
    }

    private void WriteFile(string path, byte[] data)
    {
        string full = Path.Combine(_install, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    private void AddMod(string name, string path, byte[] data) => WriteFile($"mods/{name}/USRDIR/{path}", data);

    private void Settings(string order, params string[] off) =>
        File.WriteAllText(Path.Combine(_install, "nhl_settings.ini"), $"[launcher]\nmod_order = {order}\n[mods]\n" + string.Concat(off.Select(o => $"{o} = 0\n")));

    private GameFile Find(string path = Logo)
    {
        GameFileIndex index = GameInstallLocator.Open(_install).Value!;
        return index.Find(path)!;
    }

    [Fact]
    public void TheTopEnabledModIsShown()
    {
        AddMod("Low", Logo, [5]);
        AddMod("High", Logo, [6]);
        Settings("High|Low");
        Assert.Equal("High", TextureCatalog.ShownCopy(Find(), null)!.Layer.Name);
        Assert.Equal(["High", "Low"], GameInstallLocator.EnabledMods(_install));
    }

    [Fact]
    public void ASwitchedOffModIsNotShown()
    {
        AddMod("High", Logo, [6]);
        Settings("High", "High");
        Assert.Equal(GameLayerKind.Disc, TextureCatalog.ShownCopy(Find(), null)!.Layer.Kind);
        Assert.Empty(GameInstallLocator.EnabledMods(_install));
    }

    [Fact]
    public void AnInstalledCopyOfTheModBeingEditedIsSkipped()
    {
        AddMod("Mine", Logo, [5]);
        AddMod("Other", Logo, [6]);
        Settings("Mine|Other");
        Assert.Equal("Other", TextureCatalog.ShownCopy(Find(), "mine")!.Layer.Name);
    }

    [Fact]
    public void TheOriginalStaysTheGamesOwn()
    {
        AddMod("High", Logo, [6]);
        Settings("High");
        Assert.Equal(GameLayerKind.Disc, TextureCatalog.OriginalCopy(Find())!.Layer.Kind);
    }

    [Fact]
    public void TheFingerprintFollowsSwitches()
    {
        AddMod("High", Logo, [6]);
        AddMod("Low", Logo, [5]);
        Settings("High|Low");
        string both = GameInstallLocator.EnabledModsFingerprint(_install);
        Assert.Equal(both, GameInstallLocator.EnabledModsFingerprint(_install));

        Settings("Low|High");
        string swapped = GameInstallLocator.EnabledModsFingerprint(_install);
        Assert.NotEqual(both, swapped);

        Settings("Low|High", "High");
        Assert.NotEqual(swapped, GameInstallLocator.EnabledModsFingerprint(_install));
    }

    [Fact]
    public void TheListCarriesAModsOwnFilesAndSurvivesADamagedOne()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        byte[] real;
        using (GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!)
        {
            real = game.ReadDecoded(game.Find(Logo)!.Winner).Value!;
        }

        const string added = "rendering/icesurface/centerlogo_999_cm.rpsgl";
        WriteFile("game/PS3_GAME/USRDIR/" + Logo, real);
        AddMod("High", added, real);
        AddMod("High", Logo, [9, 9, 9, 9]);
        Settings("High");

        using GameFileIndex index = GameInstallLocator.Open(_install).Value!;
        TextureCatalog catalog = TextureCatalog.Build(index);
        Assert.Contains(catalog.Files, f => f.Path == added);
        Assert.Contains(catalog.Files, f => f.Path == Logo);
        Assert.Equal(0, catalog.UnreadableFiles);
    }
}
