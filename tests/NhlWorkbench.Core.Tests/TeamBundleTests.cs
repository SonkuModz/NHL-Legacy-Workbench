using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class TeamBundleTests
{
    /// <summary>Exports a few of a team's files, runs the check, and cleans up.</summary>
    private static void WithBundle(Action<string, TeamBundleExport, GameFileIndex, DdsCodec> check)
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog teams = TeamCatalog.Build(game).Value!;
        GameTeam team = teams.Teams.First(t => t.Index == 23);
        List<TeamArt> art = team.AllArt.Where(a => a.Path.EndsWith(".rpsgl", StringComparison.OrdinalIgnoreCase)).Take(4).ToList();

        var codec = new DdsCodec();
        string parent = Directory.CreateTempSubdirectory("bundle-test").FullName;
        try
        {
            Result<TeamBundleExport> exported = TeamBundle.Export(team.Name, team.ArtId, art, p => Describe(game, p, codec), p => game.ReadDecoded(game.Find(p)!.Winner), codec, parent, TeamBundleFormat.Dds, null);
            Assert.True(exported.IsSuccess, exported.Error);
            Assert.Empty(exported.Value!.Problems);
            Assert.True(exported.Value.Pictures >= art.Count);
            check(exported.Value.Folder, exported.Value, game, codec);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    private static TextureFile? Describe(GameFileIndex game, string path, DdsCodec codec)
    {
        byte[] data = game.ReadDecoded(game.Find(path)!.Winner).Value!;
        var textures = new List<TextureInfo>();
        for (int i = 0; ; i++)
        {
            Result<GameTexture> read = GameTextures.Read(data, path, i, codec);
            if (!read.IsSuccess)
            {
                break;
            }

            textures.Add(new TextureInfo(i, $"t{i}", read.Value!.Width, read.Value.Height, read.Value.Format, read.Value.MipCount));
        }

        return textures.Count == 0 ? null : new TextureFile(path, "Test", textures);
    }

    [Fact]
    public void AnUneditedBundleChangesNothing()
    {
        WithBundle((folder, _, game, codec) =>
        {
            int saves = 0;
            Result<TeamBundleImport> imported = TeamBundle.Import(folder, p => game.Find(p) is not null, p => game.ReadDecoded(game.Find(p)!.Winner), (_, _) =>
            {
                saves++;
                return Result.Success(true);
            }, codec);
            Assert.True(imported.IsSuccess, imported.Error);
            Assert.Equal(0, imported.Value!.Pictures);
            Assert.Equal(0, saves);
            Assert.Empty(imported.Value.Problems);
            Assert.True(File.Exists(Path.Combine(folder, "README.txt")));
        });
    }

    [Fact]
    public void OnlyTheEditedPictureGoesIn()
    {
        WithBundle((folder, _, game, codec) =>
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<TeamBundleManifest>(File.ReadAllText(Path.Combine(folder, TeamBundle.ManifestName)))!;
            TeamBundleEntry entry = manifest.Entries[0];
            string picture = Path.Combine(folder, entry.File.Replace('/', Path.DirectorySeparatorChar));

            // Paint over the top of the picture, the way an outside editor would.
            DecodedDdsTexture dds = codec.ReadDdsFile(File.ReadAllBytes(picture)).Value!;
            Rgba32Image image = codec.Decode(dds.LinearData, dds.Width, dds.Height, dds.Format).Value!;
            Array.Fill(image.Pixels, (byte)200, 0, image.Pixels.Length / 2);
            byte[] edited = codec.EncodeToFormat(image, dds.Format, dds.MipCount).Value!.LinearData;
            File.WriteAllBytes(picture, codec.WrapAsDds(edited, dds.Width, dds.Height, dds.Format, dds.MipCount).Value!);

            var saved = new Dictionary<string, byte[]>();
            Result<TeamBundleImport> imported = TeamBundle.Import(folder, p => game.Find(p) is not null, p => game.ReadDecoded(game.Find(p)!.Winner), (p, b) =>
            {
                saved[p] = b;
                return Result.Success(true);
            }, codec);
            Assert.True(imported.IsSuccess, imported.Error);
            Assert.Empty(imported.Value!.Problems);
            Assert.Equal(1, imported.Value.Pictures);
            Assert.Equal(entry.GamePath, Assert.Single(saved).Key);

            GameTexture after = GameTextures.Read(saved[entry.GamePath], entry.GamePath, entry.TextureIndex, codec).Value!;
            Assert.Equal(edited, after.LinearPixelData);
        });
    }

    [Fact]
    public void ABadManifestCantReachOutside()
    {
        string folder = Directory.CreateTempSubdirectory("bundle-test").FullName;
        try
        {
            var codec = new DdsCodec();
            Assert.False(TeamBundle.Import(folder, _ => true, _ => Result.Failure<byte[]>("no"), (_, _) => Result.Success(true), codec).IsSuccess);

            File.WriteAllText(Path.Combine(folder, TeamBundle.ManifestName), "not json");
            Assert.False(TeamBundle.Import(folder, _ => true, _ => Result.Failure<byte[]>("no"), (_, _) => Result.Success(true), codec).IsSuccess);

            string outside = Path.Combine(Path.GetDirectoryName(folder)!, "outside.dds");
            var manifest = new TeamBundleManifest
            {
                Entries =
                [
                    new TeamBundleEntry { GamePath = "a/b.rpsgl", File = "../outside.dds", Sha256 = "x" },
                    new TeamBundleEntry { GamePath = "../../escape", File = "here.dds", Sha256 = "x" },
                ],
            };
            File.WriteAllBytes(Path.Combine(folder, "here.dds"), [1, 2, 3]);
            File.WriteAllText(Path.Combine(folder, TeamBundle.ManifestName), System.Text.Json.JsonSerializer.Serialize(manifest));

            bool saved = false;
            Result<TeamBundleImport> imported = TeamBundle.Import(folder, p => p == "a/b.rpsgl", _ => Result.Failure<byte[]>("no"), (_, _) =>
            {
                saved = true;
                return Result.Success(true);
            }, codec);
            Assert.True(imported.IsSuccess);
            Assert.False(saved);
            Assert.False(File.Exists(outside));
            Assert.Equal(2, imported.Value!.Problems.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
