using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.Core.GameFiles;

/// <summary>What one texture in a game file is, without its pixels.</summary>
public sealed record TextureInfo(int Index, string Name, int Width, int Height, TextureFormat Format, int MipCount);

/// <summary>A game file that holds at least one texture.</summary>
public sealed record TextureFile(string Path, string Category, IReadOnlyList<TextureInfo> Textures)
{
    /// <summary>The texture shown first: the largest, matching what a picture replaces by default.</summary>
    public TextureInfo Main => Textures.MaxBy(t => (long)t.Width * t.Height)!;
}

/// <summary>Every texture file in the game, plus what was left out and why.</summary>
public sealed record TextureCatalog(IReadOnlyList<TextureFile> Files, int FilesWithoutTextures, int UnreadableFiles)
{
    /// <summary>The categories in display order. Anything unrecognized lands in the last one.</summary>
    public static readonly IReadOnlyList<string> Categories = ["Menu art", "Ice and rink", "Logos", "Uniforms", "Goalie gear", "Player gear", "People", "Arena", "Effects", "Other"];

    private static readonly string[] JerseyRoles = ["Numbers", "Numbers normal", "Numbers shine", "Jersey", "Jersey shine", "Jersey normal"];
    private static readonly string[] PantRoles = ["Pants", "Pants normal", "Pants shine"];

    private static readonly Dictionary<string, string> FolderCategories = new(StringComparer.Ordinal)
    {
        ["icesurface"] = "Ice and rink", ["ice"] = "Ice and rink", ["board"] = "Ice and rink", ["adboard"] = "Ice and rink", ["net"] = "Ice and rink", ["puck"] = "Ice and rink",
        ["logo"] = "Logos", ["czlogo"] = "Logos",
        ["jersey"] = "Uniforms", ["jersey_common"] = "Uniforms", ["pant"] = "Uniforms", ["sock"] = "Uniforms", ["czjersey"] = "Uniforms", ["czpant"] = "Uniforms", ["czsock"] = "Uniforms", ["czfont"] = "Uniforms",
        ["blocker"] = "Goalie gear", ["trapper"] = "Goalie gear", ["goaliepad"] = "Goalie gear", ["goaliehelmet"] = "Goalie gear", ["goaliestick"] = "Goalie gear",
        ["helmet"] = "Player gear", ["glove"] = "Player gear", ["skate"] = "Player gear", ["playerstick"] = "Player gear",
        ["player"] = "People", ["player_common"] = "People", ["body"] = "People", ["czhead"] = "People", ["czhair"] = "People", ["coach"] = "People", ["referee"] = "People", ["trainer"] = "People", ["commissioner"] = "People",
        ["crowd"] = "Arena", ["banner"] = "Arena", ["jumbotron"] = "Arena", ["tunnel"] = "Arena", ["spotlight"] = "Arena", ["enviro"] = "Arena", ["prop"] = "Arena", ["shotclock"] = "Arena", ["waterbottle"] = "Arena",
        ["effects"] = "Effects", ["particle"] = "Effects", ["visualfx"] = "Effects",
    };

    public static string CategoryFor(string gamePath)
    {
        if (IsMenuArt(gamePath))
        {
            return "Menu art";
        }

        string[] parts = gamePath.Split('/');
        return parts.Length >= 3 && FolderCategories.TryGetValue(parts[1], out string? category) ? category : "Other";
    }

    private static bool IsMenuArt(string gamePath) =>
        gamePath.StartsWith("fe/ion/artassets/", StringComparison.Ordinal) && MenuArt.IsPackage(gamePath);

    /// <summary>
    /// What a texture in a uniform file is for. PS3 files don't name their textures, so this goes by position, which
    /// the tests check across every stock kit: a jersey holds the numbers sheet then the jersey, each as color, normal, shine.
    /// </summary>
    public static string? RoleFor(string gamePath, int textureIndex, int textureCount)
    {
        if (gamePath.StartsWith("rendering/jersey/texlib_", StringComparison.Ordinal) && textureCount == 6)
        {
            return JerseyRoles[textureIndex];
        }

        return gamePath.StartsWith("rendering/pant/texlib_", StringComparison.Ordinal) && textureCount == 3 ? PantRoles[textureIndex] : null;
    }

    /// <summary>The game's own copy of a file, ignoring anything an installed mod put on top of it.</summary>
    public static GameFileCopy? OriginalCopy(GameFile file) => file.Copies.FirstOrDefault(c => c.Layer.Kind != GameLayerKind.Mod);

    /// <summary>
    /// The copy the launcher would use, leaving out an installed copy of the mod being edited, since the working copy
    /// of that mod takes its place. Enabled mods come first, best first, then the update, then the disc.
    /// </summary>
    public static GameFileCopy? ShownCopy(GameFile file, string? ownMod) =>
        file.Copies.FirstOrDefault(c => c.Layer.Kind != GameLayerKind.Mod || !c.Layer.Name.Equals(ownMod, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the header of every texture file, as the launcher's enabled mods have them. Takes a few seconds, so run it off the UI thread.</summary>
    public static TextureCatalog Build(GameFileIndex index, CancellationToken cancel = default, Action<int>? progress = null)
    {
        GameFile[] candidates = index.Files.Where(f => f.Path.EndsWith(".rpsgl", StringComparison.Ordinal) || IsMenuArt(f.Path)).ToArray();
        var found = new TextureFile?[candidates.Length];
        int unreadable = 0, done = 0;

        Parallel.For(0, candidates.Length, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, i =>
        {
            GameFile file = candidates[i];

            // A damaged mod file shouldn't take the texture off the list, so the game's copy is the fallback.
            bool read = false;
            foreach (GameFileCopy copy in new[] { file.Winner, OriginalCopy(file) }.OfType<GameFileCopy>().Distinct())
            {
                Result<byte[]> data = index.ReadDecoded(copy);
                if (data.IsSuccess && TryList(file.Path, data.Value!, out found[i]))
                {
                    read = true;
                    break;
                }
            }

            if (!read)
            {
                Interlocked.Increment(ref unreadable);
            }

            int finished = Interlocked.Increment(ref done);
            if (finished % 250 == 0)
            {
                progress?.Invoke(finished);
            }
        });

        List<TextureFile> files = found.OfType<TextureFile>().OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new TextureCatalog(files, candidates.Length - files.Count - unreadable, unreadable);
    }

    /// <summary>False if the bytes can't be read. Plenty of menu packages hold only layout data, which reads fine and lists nothing.</summary>
    private static bool TryList(string path, byte[] data, out TextureFile? listed)
    {
        listed = null;
        if (MenuArt.IsPackage(path))
        {
            IReadOnlyList<TextureInfo> pictures = MenuArt.List(data, path);
            if (pictures.Count > 0)
            {
                listed = new TextureFile(path, CategoryFor(path), pictures);
            }

            return true;
        }

        Result<RpsglContainer> parsed = RpsglReader.Parse(data, path);
        if (!parsed.IsSuccess)
        {
            return false;
        }

        if (parsed.Value!.Textures.Count > 0)
        {
            listed = new TextureFile(
                path,
                CategoryFor(path),
                parsed.Value.Textures.Select(t => new TextureInfo(t.Index, t.Name, t.Width, t.Height, t.Format, t.MipCount)).ToList());
        }

        return true;
    }
}
