using System.Text;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class GameFileIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nhlwb-index-{Guid.NewGuid():N}");

    public GameFileIndexTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    private void Write(string relativePath, byte[] data)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private void Write(string relativePath, string text) => Write(relativePath, Text(text));

    private GameFileIndex OpenInstall()
    {
        Result<GameFileIndex> result = GameInstallLocator.Open(_root);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private string Winner(GameFileIndex index, string path) =>
        Encoding.ASCII.GetString(index.ReadDecoded(index.Find(path)!.Winner).Value!);

    private void WriteBasicInstall(string settings = "")
    {
        Write("nhl_settings.ini", settings);
        Write("game/PS3_GAME/USRDIR/cacherender.big", EbTestData.EbArchive([("rendering/logo/a.rpsgl", Text("disc member"))]));
    }

    [Fact]
    public void LooseFileBeatsArchiveMember()
    {
        WriteBasicInstall();
        Write("game/PS3_GAME/USRDIR/rendering/logo/a.rpsgl", "disc loose");

        using GameFileIndex index = OpenInstall();
        GameFile file = index.Find("rendering/logo/a.rpsgl")!;

        Assert.Equal(2, file.Copies.Count);
        Assert.True(file.Winner.IsLoose);
        Assert.False(file.WinnerIsUncertain);
        Assert.Equal("disc loose", Winner(index, file.Path));
    }

    [Fact]
    public void LayersRankModThenUpdateThenDisc()
    {
        WriteBasicInstall();
        Write("game/PS3_GAME/USRDIR/x.bin", "disc");
        Write("data/update/USRDIR/x.bin", "update");
        Write("mods/M/USRDIR/x.bin", "mod");

        using GameFileIndex index = OpenInstall();

        Assert.Equal([GameLayerKind.Mod, GameLayerKind.Update, GameLayerKind.Disc], index.Find("x.bin")!.Copies.Select(c => c.Layer.Kind));
        Assert.Equal("mod", Winner(index, "x.bin"));
    }

    [Fact]
    public void ModOrderFollowsTheLauncherListAndTopWins()
    {
        WriteBasicInstall("[launcher]\nmod_order = Bottom|\n");
        WriteBasicInstall("[launcher]\nmod_order = Top|Bottom\n");
        Write("mods/Bottom/USRDIR/x.bin", "bottom");
        Write("mods/Top/USRDIR/x.bin", "top");
        Write("mods/Alpha/USRDIR/x.bin", "alpha, unlisted");

        using GameFileIndex index = OpenInstall();

        Assert.Equal(["Top", "Bottom", "Alpha"], index.Find("x.bin")!.Copies.Select(c => c.Layer.Name));
    }

    [Fact]
    public void DisabledModsAndStagingFolderAreLeftOut()
    {
        WriteBasicInstall("[mods]\nOff = 0\nOn = 1\n");
        Write("mods/Off/USRDIR/off.bin", "x");
        Write("mods/On/USRDIR/on.bin", "x");
        Write("mods/.incoming/USRDIR/staged.bin", "x");

        using GameFileIndex index = OpenInstall();

        Assert.Null(index.Find("off.bin"));
        Assert.Null(index.Find("staged.bin"));
        Assert.NotNull(index.Find("on.bin"));
    }

    [Fact]
    public void ComposedHdd0FolderIsIgnored()
    {
        WriteBasicInstall();
        Write("data/hdd0/game/BLUS31540/USRDIR/leftover.bin", "x");

        using GameFileIndex index = OpenInstall();

        Assert.Null(index.Find("leftover.bin"));
        Assert.DoesNotContain(index.Files, f => f.Path.Contains("hdd0"));
    }

    [Fact]
    public void ModRosterFolderIsNotAGameFile()
    {
        WriteBasicInstall();
        Write("mods/R/USRDIR/x.bin", "x");
        Write("mods/R/roster/SYS-DATA", "roster");

        using GameFileIndex index = OpenInstall();

        Assert.DoesNotContain(index.Files, f => f.Path.Contains("sys-data"));
    }

    [Fact]
    public void ModCopyOfAnArchiveReplacesItsMembers()
    {
        WriteBasicInstall();
        Write("mods/M/USRDIR/cacherender.big", EbTestData.EbArchive([("rendering/new.rpsgl", Text("mod member"))]));

        using GameFileIndex index = OpenInstall();

        Assert.Null(index.Find("rendering/logo/a.rpsgl"));
        Assert.Equal("mod member", Winner(index, "rendering/new.rpsgl"));
        Assert.Equal(2, index.Find("cacherender.big")!.Copies.Count);
    }

    [Fact]
    public void SamePathInTwoArchivesIsMarkedUncertain()
    {
        WriteBasicInstall();
        Write("data/update/USRDIR/patch.big", EbTestData.EbArchive([("rendering/logo/a.rpsgl", Text("patched"))]));

        using GameFileIndex index = OpenInstall();
        GameFile file = index.Find("rendering/logo/a.rpsgl")!;

        Assert.True(file.WinnerIsUncertain);
        Assert.Equal("patch.big", file.Winner.ArchivePath);
        Assert.Equal("patched", Winner(index, file.Path));
    }

    [Fact]
    public void BigFilesOutsideMountFoldersStayWhole()
    {
        WriteBasicInstall();
        Write("mods/M/USRDIR/fe/overlays/overlay.big", EbTestData.EbArchive([("inner.bin", Text("inner"))]));

        using GameFileIndex index = OpenInstall();

        Assert.NotNull(index.Find("fe/overlays/overlay.big"));
        Assert.Null(index.Find("inner.bin"));

        Result<NestedArchive> nested = index.OpenNested(index.Find("fe/overlays/overlay.big")!.Winner);
        Assert.True(nested.IsSuccess, nested.Error);
        Assert.Equal("inner", Encoding.ASCII.GetString(nested.Value!.Read("inner.bin").Value!));
    }

    [Fact]
    public void MemberWithChunkZipDecodesThroughTheIndex()
    {
        byte[] original = new byte[5000];
        Write("nhl_settings.ini", string.Empty);
        Write("game/PS3_GAME/USRDIR/cache.big", EbTestData.EbArchive([("fe/z.bin", EbTestData.ChunkZipNewer(original, 1024))]));

        using GameFileIndex index = OpenInstall();
        GameFileCopy copy = index.Find("fe/z.bin")!.Winner;

        Assert.Equal(original, index.ReadDecoded(copy).Value);
        Assert.NotEqual(original.Length, index.ReadStored(copy).Value!.Length);
    }

    [Fact]
    public void FolderInstallUsesGameSourceSetting()
    {
        string source = Path.Combine(_root, "elsewhere");
        Write("nhl_settings.ini", $"[launcher]\ngame_source = {source}\n");
        Write("elsewhere/PS3_GAME/USRDIR/x.bin", "from source folder");

        using GameFileIndex index = OpenInstall();

        Assert.Equal("from source folder", Winner(index, "x.bin"));
    }

    [Fact]
    public void InstallWithoutDiscFilesFailsClearly()
    {
        Write("nhl_settings.ini", string.Empty);

        Result<GameFileIndex> result = GameInstallLocator.Open(_root);

        Assert.False(result.IsSuccess);
        Assert.Contains("no disc files", result.Error);
    }

    [Theory]
    [InlineData("PS3_GAME/USRDIR/x.bin", "")]
    [InlineData("USRDIR/x.bin", "")]
    [InlineData("PS3_GAME/USRDIR/x.bin", "PS3_GAME")]
    public void ExtractedDiscFolderOpens(string file, string openFrom)
    {
        Write(file, "x");

        Result<GameFileIndex> result = GameInstallLocator.Open(Path.Combine(_root, openFrom));

        Assert.True(result.IsSuccess, result.Error);
        using GameFileIndex index = result.Value!;
        Assert.NotNull(index.Find("x.bin"));
        Assert.Equal(GameLayerKind.Disc, Assert.Single(index.Layers).Kind);
    }

    [Fact]
    public void UnrelatedFolderAndMissingPathFailClearly()
    {
        Write("notes.txt", "x");

        Assert.False(GameInstallLocator.Open(_root).IsSuccess);
        Assert.False(GameInstallLocator.Open(Path.Combine(_root, "notes.txt")).IsSuccess);
        Assert.False(GameInstallLocator.Open(Path.Combine(_root, "missing")).IsSuccess);
    }

    [Fact]
    public void DiscImageOpens()
    {
        string iso = Path.Combine(_root, "disc.iso");
        File.WriteAllBytes(iso, EbTestData.IsoImage(Text("1"), new byte[2048], Text("2")));

        Result<IReadOnlyList<GameLayer>> layers = GameInstallLocator.ResolveLayers(iso);

        Assert.True(layers.IsSuccess, layers.Error);
        Assert.Equal(GameLayerKind.Disc, Assert.Single(layers.Value!).Kind);
        Assert.Empty(layers.Value![0].Source.Files); // the sample image has no PS3_GAME/USRDIR
    }

    [Fact]
    public void ShellLeftoversAreIgnored()
    {
        WriteBasicInstall();
        Write("mods/M/USRDIR/x.bin", "x");
        Write("mods/M/USRDIR/desktop.ini", "x");
        Write("mods/M/USRDIR/Thumbs.db", "x");
        Write("mods/M/USRDIR/._x.bin", "x");

        using GameFileIndex index = OpenInstall();

        Assert.Null(index.Find("desktop.ini"));
        Assert.Null(index.Find("thumbs.db"));
        Assert.Null(index.Find("._x.bin"));
    }

    [Fact]
    public void UnsafeMemberPathIsSkippedWithAWarning()
    {
        Write("nhl_settings.ini", string.Empty);
        Write("game/PS3_GAME/USRDIR/cache.big", EbTestData.EbArchive([("../../evil.bin", Text("x")), ("fine.bin", Text("y"))]));

        using GameFileIndex index = OpenInstall();

        Assert.DoesNotContain(index.Files, f => f.Path.Contains("evil"));
        Assert.NotNull(index.Find("fine.bin"));
        Assert.Contains(index.Warnings, w => w.Message.Contains("evil"));
    }

    [Fact]
    public void BrokenArchiveWarnsAndTheRestStillIndexes()
    {
        Write("nhl_settings.ini", string.Empty);
        byte[] broken = EbTestData.EbArchive([("a.bin", Text("x"))]);
        EbTestData.PutU32(broken, 0x08, 0x1234);
        Write("game/PS3_GAME/USRDIR/cache.big", broken);
        Write("game/PS3_GAME/USRDIR/other.bin", "fine");

        using GameFileIndex index = OpenInstall();

        Assert.NotNull(index.Find("other.bin"));
        Assert.NotNull(index.Find("cache.big"));
        Assert.Contains(index.Warnings, w => w.Message.Contains("unknown EB variant"));
    }

    [Theory]
    [InlineData(@"Rendering\Logo\A.RPSGL", "rendering/logo/a.rpsgl")]
    [InlineData("./decisionnode.dtd", "decisionnode.dtd")]
    [InlineData("a//b/./c", "a/b/c")]
    [InlineData("../x", null)]
    [InlineData("a/../../x", null)]
    [InlineData("C:/x", null)]
    [InlineData("", null)]
    public void NormalizesPaths(string input, string? expected)
    {
        Assert.Equal(expected, GameFileIndex.NormalizePath(input));
    }

    [Fact]
    public void SearchMatchesAllWordsAndRanksFileNameMatchesFirst()
    {
        Write("nhl_settings.ini", string.Empty);
        Write("game/PS3_GAME/USRDIR/cache.big", EbTestData.EbArchive(
        [
            ("rendering/jersey/other.rpsgl", Text("x")),
            ("aaa/jersey_home.rpsgl", Text("x")),
            ("rendering/logo/z.rpsgl", Text("x")),
        ]));

        using GameFileIndex index = OpenInstall();

        Assert.Equal(["aaa/jersey_home.rpsgl", "rendering/jersey/other.rpsgl"], index.Search("JERSEY rpsgl").Select(f => f.Path));
        Assert.Single(index.Search("jersey", limit: 1));
        Assert.Empty(index.Search("   "));
        Assert.Empty(index.Search("nothing-matches"));
    }

    [Fact]
    public void Big4ParsesButRefusesRebuild()
    {
        byte[] bigf = BuildBig4();

        Result<BigfArchive> parsed = BigfArchive.Parse(bigf, "gps.big");

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.False(BigfArchive.IsBigf(bigf));
        Assert.Equal(Text("payload"), parsed.Value!.GetEntryData("RALL"));
        Assert.Throws<NotSupportedException>(() => parsed.Value.RebuildReplacingEntry("RALL", Text("new")));
    }

    [Fact]
    public void NestedArchiveRejectsOtherData()
    {
        Assert.False(NestedArchive.IsArchive(Text("just some text here")));
        Assert.False(NestedArchive.Open(Text("just some text here"), "x").IsSuccess);
    }

    [Fact]
    public void RealDisc_IndexMatchesTheKnownCounts()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        Result<GameFileIndex> result = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc);
        Assert.True(result.IsSuccess, result.Error);
        using GameFileIndex index = result.Value!;

        Assert.Equal(16, index.MountedArchives.Count());
        Assert.NotNull(index.Find("rendering/jersey/jersey_0_0_2_0_0_0.rpsgl"));
        Assert.NotNull(index.Find(@"GAMEDATA\AIDATA.BIG"));

        // GPS.BIG is BIG4, so its 15 members stay nested.
        Result<NestedArchive> gps = index.OpenNested(index.Find("gamedata/gps.big")!.Winner);
        Assert.True(gps.IsSuccess, gps.Error);
        Assert.Equal(15, gps.Value!.Entries.Count);
    }

    /// <summary>
    /// This file on the retail disc was damaged before it shipped: every 0x0A byte became 0x0D 0x0A,
    /// as a text-mode write does on Windows. Its tables are misaligned, so it must not open as an
    /// archive with wrong names. Undoing the damage shows the reader's refusal is correct.
    /// </summary>
    [Fact]
    public void RealDisc_ScenarioOnlineIsDamagedOnTheDiscAndIsRefused()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex index = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        GameFileCopy copy = index.Find("scenario/scenario_online.big")!.Winner;

        Assert.False(index.OpenNested(copy).IsSuccess);

        byte[] damaged = index.ReadDecoded(copy).Value!;
        byte[] restored = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(damaged).Replace("\r\n", "\n"));
        long declaredSize = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(restored.AsSpan(0x18, 8));
        Assert.Equal(declaredSize, restored.Length);

        Result<NestedArchive> repaired = NestedArchive.Open(restored, copy.Path);
        Assert.True(repaired.IsSuccess, repaired.Error);
        Assert.Equal(11, repaired.Value!.Entries.Count);
        Assert.Contains(repaired.Value.Entries, e => e.Name == "./scenario_set.json");
    }

    [Fact]
    public void RealDisc_RinkLogoReadsAsATextureContainer()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex index = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;

        GameFile logo = Assert.Single(index.Search("centerlogo_0_cm"));
        Result<byte[]> data = index.ReadDecoded(logo.Winner);

        Assert.True(data.IsSuccess, data.Error);
        Assert.True(NhlWorkbench.Core.Formats.Rpsgl.RpsglReader.IsRpsgl(data.Value!));
    }

    private static byte[] BuildBig4()
    {
        byte[] name = Text("RALL\0");
        byte[] payload = Text("payload");
        int toc = 16 + 8 + name.Length;
        int offset = (toc + 63) / 64 * 64;
        var data = new byte[offset + payload.Length];
        "BIG4"u8.CopyTo(data);
        EbTestData.PutU32(data, 8, 1);
        EbTestData.PutU32(data, 12, (uint)toc);
        EbTestData.PutU32(data, 16, (uint)offset);
        EbTestData.PutU32(data, 20, (uint)payload.Length);
        name.CopyTo(data, 24);
        payload.CopyTo(data, offset);
        return data;
    }
}
