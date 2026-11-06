using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class PictureFolderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("picture-folder").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private static TextureFile Fake(string path, int textures = 1) =>
        new(path, "Test", Enumerable.Range(0, textures).Select(i => new TextureInfo(i, $"t{i}", 4, 4, default, 1)).ToList());

    private void Add(string relative)
    {
        string path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }

    [Fact]
    public void PicturesGoByName()
    {
        Add("jersey_0_15_0.png");
        Add("sheet_2.dds");
        Add("deep/down/logo.jpg");
        Add("notes.txt");
        Add("bundle.json");

        PictureFolderMatch match = PictureFolder.Match(_folder, [Fake("rendering/jersey/jersey_0_15_0.rpsgl"), Fake("rendering/x/sheet.rpsgl", 3), Fake("rendering/logo/logo.rpsgl")]).Value!;

        Assert.Empty(match.Problems);
        Assert.Equal(3, match.Edits.Count);
        Assert.Contains(match.Edits, e => e.GamePath == "rendering/jersey/jersey_0_15_0.rpsgl" && e.TextureIndex == 0);
        Assert.Contains(match.Edits, e => e.GamePath == "rendering/x/sheet.rpsgl" && e.TextureIndex == 1);
        Assert.Contains(match.Edits, e => e.GamePath == "rendering/logo/logo.rpsgl");
    }

    [Fact]
    public void AWholeNameBeatsATextureNumber()
    {
        Add("logo_1.png");
        PictureFolderMatch match = PictureFolder.Match(_folder, [Fake("a/logo_1.rpsgl"), Fake("a/logo.rpsgl", 3)]).Value!;
        Assert.Equal("a/logo_1.rpsgl", Assert.Single(match.Edits).GamePath);
    }

    [Fact]
    public void WhatCantBePlacedIsExplained()
    {
        Add("nothing.png");
        Add("sheet.png");
        Add("sheet_9.png");
        Add("twin.png");
        Add("copy1/solo.png");
        Add("copy2/solo.png");

        PictureFolderMatch match = PictureFolder.Match(_folder, [Fake("a/sheet.rpsgl", 2), Fake("a/twin.rpsgl"), Fake("b/twin.rpsgl"), Fake("c/solo.rpsgl")]).Value!;

        Assert.Equal("c/solo.rpsgl", Assert.Single(match.Edits).GamePath);
        Assert.Equal(5, match.Problems.Count);
        Assert.Contains(match.Problems, p => p.StartsWith("nothing.png") && p.Contains("no texture"));
        Assert.Contains(match.Problems, p => p.StartsWith("sheet.png") && p.Contains("sheet_1"));
        Assert.Contains(match.Problems, p => p.StartsWith("sheet_9.png") && p.Contains("only holds 2"));
        Assert.Contains(match.Problems, p => p.StartsWith("twin.png") && p.Contains("2 game folders"));
        Assert.Contains(match.Problems, p => p.StartsWith("copy2/solo.png") && p.Contains("already"));
    }

    [Fact]
    public void AFolderNameSettlesATie()
    {
        Add("b/twin.png");
        PictureFolderMatch match = PictureFolder.Match(_folder, [Fake("a/twin.rpsgl"), Fake("b/twin.rpsgl")]).Value!;
        Assert.Equal("b/twin.rpsgl", Assert.Single(match.Edits).GamePath);
    }

    [Fact]
    public void AMissingFolderIsAFailure() =>
        Assert.False(PictureFolder.Match(Path.Combine(_folder, "nowhere"), []).IsSuccess);

    [Fact]
    public void ARealPictureGoesIn()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog teams = TeamCatalog.Build(game).Value!;
        string gamePath = teams.Teams.First(t => t.Index == 23).AllArt.First(a => a.Path.EndsWith(".rpsgl", StringComparison.OrdinalIgnoreCase)).Path;
        var codec = new DdsCodec();
        byte[] original = game.ReadDecoded(game.Find(gamePath)!.Winner).Value!;
        ExportedDds dds = GameTextures.Export(original, gamePath, codec, 0).Value!;
        File.WriteAllBytes(Path.Combine(_folder, Path.GetFileNameWithoutExtension(gamePath) + ".dds"), dds.Bytes);

        PictureFolderMatch match = PictureFolder.Match(_folder, [Fake(gamePath)]).Value!;
        byte[]? saved = null;
        (int pictures, IReadOnlyList<string> changed, IReadOnlyList<string> problems) = PictureFolder.Apply(match.Edits, _ => Result.Success(original), (_, bytes) =>
        {
            saved = bytes;
            return Result.Success(true);
        }, codec);

        Assert.Empty(problems);
        Assert.Equal(1, pictures);
        Assert.Equal(gamePath, Assert.Single(changed));
        Assert.Equal(GameTextures.Read(original, gamePath, 0, codec).Value!.LinearPixelData, GameTextures.Read(saved!, gamePath, 0, codec).Value!.LinearPixelData);
    }
}
