using Xunit;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tests.TestData;

namespace NhlWorkbench.Core.Tests;

/// <summary>Checks the team links against the real game. They skip when the disc image isn't on this machine.</summary>
public class TeamCatalogTests
{
    [Fact]
    public void EveryTeamComesFromTheGameDatabaseWithItsName()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        List<GameTeam> teams = catalog.Teams.Where(t => t.HasDatabaseRow).ToList();

        // 252 rows, less the 30 blank create-a-team rows.
        Assert.Equal(222, teams.Count);
        Assert.Equal(32, teams.Count(t => t.League == "NHL"));
        Assert.Equal("NHL", catalog.Teams[0].League);
        Assert.DoesNotContain(teams, t => t.Name.Length == 0 || t.Name.Contains('®') || t.Name.Contains('™'));
        Assert.DoesNotContain(teams, t => t.ArtId == 20054);

        GameTeam toronto = catalog.TeamWithArt(27)!;
        Assert.Equal("Toronto Maple Leafs", toronto.Name);
        Assert.Equal("TOR", toronto.Abbreviation);
        Assert.Equal("Air Canada Centre", toronto.ArenaName);
        Assert.Equal(new TeamColor(0, 78, 159), toronto.Primary);
        Assert.Equal("Anaheim Ducks", catalog.TeamWithArt(0)!.Name);
        Assert.Equal("Arizona Coyotes", catalog.TeamWithArt(22)!.Name);
    }

    [Fact]
    public void EveryListedKitHasItsWholeUniform()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        List<TeamKit> listed = catalog.Teams.SelectMany(t => t.Kits).Where(k => k.IsListed).ToList();

        Assert.Equal(703, listed.Count);
        foreach (string piece in new[] { "Jersey", "Pants", "Socks" })
        {
            Assert.All(listed, k => Assert.Contains(k.Pieces, p => p.Label == piece));
        }

        // Every path named really is in the game.
        Assert.All(listed.SelectMany(k => k.Pieces), p => Assert.NotNull(game.Find(p.Path)));
    }

    [Fact]
    public void ABundleHoldsTheTeamsLogosIceBuildingAndKits()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        GameTeam leafs = catalog.TeamWithArt(27)!;
        var paths = leafs.AllArt.Select(a => a.Path).ToHashSet();

        Assert.Equal("fe/ion/artassets/teamlogos/t27.big", leafs.CrestPath);
        Assert.Contains("rendering/icesurface/centerlogo_27_cm.rpsgl", paths);
        Assert.Contains("rendering/puck/puck_27_cm.rpsgl", paths);
        Assert.Contains("rendering/banner/banner_27_cm.rpsgl", paths);
        Assert.Contains(leafs.Art, a => a.Group == "Team logo sets");
        Assert.Contains(leafs.Art, a => a.Label == "Jumbotron");
        Assert.Contains(leafs.Art, a => a.Section == TeamSection.GoalieMasks);
        Assert.Contains(leafs.Kits, k => k.Pieces.Any(p => p.Path == "rendering/jersey/texlib_1_27_4.rpsgl"));

        Assert.Equal("Toronto Maple Leafs", catalog.OwnersOf("rendering/puck/puck_27_cm.rpsgl").Single().Team.Name);
        Assert.Contains("rendering/puck/puck_27_cm.rpsgl", catalog.PathsForTeams("maple leafs"));
        Assert.Contains("rendering/puck/puck_27_cm.rpsgl", catalog.PathsForTeams("tor"));
    }

    [Fact]
    public void UniformTexturesAreInTheSameOrderInEveryStockFile()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TextureCatalog catalog = TextureCatalog.Build(game);
        List<TextureFile> jerseys = catalog.Files.Where(f => f.Path.StartsWith("rendering/jersey/texlib_", StringComparison.Ordinal) && f.Textures.Count == 6).ToList();
        List<TextureFile> pants = catalog.Files.Where(f => f.Path.StartsWith("rendering/pant/texlib_", StringComparison.Ordinal) && f.Textures.Count == 3).ToList();

        Assert.True(jerseys.Count > 800);
        Assert.True(pants.Count > 800);
        Assert.All(jerseys, f =>
        {
            Assert.Equal(TextureFormat.Dxt5, f.Textures[0].Format);
            Assert.Equal(f.Textures[0].Width, f.Textures[1].Width);
            Assert.True(f.Textures[3].Width > f.Textures[0].Width, f.Path);
            Assert.Equal(TextureFormat.Dxt1, f.Textures[3].Format);
            Assert.Equal(f.Textures[3].Width, f.Textures[4].Width);
            Assert.Equal(f.Textures[3].Width, f.Textures[5].Width);
        });
        Assert.All(pants, f => Assert.True(f.Textures[0].Width >= f.Textures[2].Width, f.Path));

        Assert.Equal("Numbers", TextureCatalog.RoleFor(jerseys[0].Path, 0, 6));
        Assert.Equal("Jersey", TextureCatalog.RoleFor(jerseys[0].Path, 3, 6));
        Assert.Equal("Pants", TextureCatalog.RoleFor(pants[0].Path, 0, 3));
        Assert.Null(TextureCatalog.RoleFor("rendering/logo/texlib_0_0_0.rpsgl", 0, 3));
    }

    [Fact]
    public void WinterClassicRowsShareArtButKeepTheirOwnKit()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        GameTeam club = catalog.TeamWithArt(19)!;
        GameTeam classic = catalog.Teams.Single(t => t.ArtId == 19 && t.League == "Winter Classic");

        Assert.Equal("New York Rangers", club.Name);
        Assert.Equal("New York Rangers (Winter Classic)", classic.Name);
        Assert.Equal(10, club.Kits.Count(k => k.IsListed));
        Assert.Single(classic.Kits);
        Assert.Equal(2, catalog.OwnersOf(club.CrestPath!).Count);
    }

    [Fact]
    public void ArtNoTeamUsesStaysReachable()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        TeamCatalog catalog = TeamCatalog.Build(game).Value!;
        List<GameTeam> unlisted = catalog.Teams.Where(t => !t.HasDatabaseRow).ToList();

        Assert.NotEmpty(unlisted);
        Assert.All(unlisted, t => Assert.Equal(TeamCatalog.UnlistedLeague, t.League));
        Assert.Equal(catalog.Teams.Count - unlisted.Count, catalog.Teams.TakeWhile(t => t.HasDatabaseRow).Count());
    }

    [Fact]
    public void MenuArtRoundTripsThroughDdsAndPicture()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        const string Path = "fe/ion/artassets/teamlogos/t27.big";
        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        byte[] package = game.ReadDecoded(TextureCatalog.OriginalCopy(game.Find(Path)!)!).Value!;
        var codec = new DdsCodec();

        TextureInfo info = Assert.Single(MenuArt.List(package, Path));
        GameTexture original = GameTextures.Read(package, Path, 0, codec).Value!;
        Assert.Equal(info.Width, original.Width);
        Assert.Equal(info.Format, original.Format);

        // The same DDS going back in changes no pixels, and the rest of the package stays as it was.
        ExportedDds exported = GameTextures.Export(package, Path, codec, 0).Value!;
        byte[] same = GameTextures.Import(package, Path, exported.Bytes, codec, 0).Value!;
        Assert.Equal(original.LinearPixelData, GameTextures.Read(same, Path, 0, codec).Value!.LinearPixelData);
        Assert.Equal(MenuArt.List(package, Path), MenuArt.List(same, Path));

        // A picture fits to the same size and format, and the package keeps its other members.
        var red = new byte[64 * 64 * 4];
        for (int i = 0; i < red.Length; i += 4)
        {
            red[i] = 255;
            red[i + 3] = 255;
        }

        byte[] swapped = GameTextures.Replace(package, Path, new Rgba32Image { Width = 64, Height = 64, Pixels = red }, codec, 0).Value!;
        GameTexture after = GameTextures.Read(swapped, Path, 0, codec).Value!;
        Assert.Equal((original.Width, original.Height, original.Format, original.MipCount), (after.Width, after.Height, after.Format, after.MipCount));
        Assert.NotEqual(original.LinearPixelData, after.LinearPixelData);

        NestedArchive before = NestedArchive.Open(package, Path).Value!;
        NestedArchive now = NestedArchive.Open(swapped, Path).Value!;
        Assert.Equal(before.Entries.Select(e => e.Name), now.Entries.Select(e => e.Name));
        foreach (NestedEntry entry in before.Entries.Where(e => e.StoredSize > 0))
        {
            byte[] old = before.Read(entry.Name).Value!;
            if (!TextureDds.IsDds(old))
            {
                Assert.Equal(old, now.Read(entry.Name).Value!);
            }
        }
    }
}
