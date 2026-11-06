using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

public enum TeamBundleFormat
{
    /// <summary>The game's own format and mips. Comes back exactly as it left.</summary>
    Dds,

    /// <summary>Easier to paint on. Compressed and given mips again on the way back.</summary>
    Png,
}

/// <summary>A picture in a bundle folder and the texture it came from.</summary>
public sealed class TeamBundleEntry
{
    public string GamePath { get; set; } = string.Empty;

    public int TextureIndex { get; set; }

    /// <summary>Its place in the bundle folder, with forward slashes.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>The picture's hash when exported, so an import can tell what was edited.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>What's written to <c>bundle.json</c>.</summary>
public sealed class TeamBundleManifest
{
    public int FormatVersion { get; set; } = 1;

    public string Team { get; set; } = string.Empty;

    public int ArtId { get; set; }

    public List<TeamBundleEntry> Entries { get; set; } = [];
}

/// <summary>What an export wrote.</summary>
public sealed record TeamBundleExport(string Folder, int Pictures, IReadOnlyList<string> Problems);

/// <summary>What an import did. Only pictures that differ from the export count as edited.</summary>
public sealed record TeamBundleImport(string Team, int Pictures, int Files, IReadOnlyList<string> Problems, IReadOnlyList<string> ChangedPaths);

/// <summary>
/// A whole team's art as a folder of pictures to edit outside the app: logos, uniforms, ice and arena, sorted by what they
/// are. Importing the folder puts back only what was edited.
/// </summary>
public static class TeamBundle
{
    public const string ManifestName = "bundle.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes the art to a new folder inside <paramref name="parent"/> named for the team.</summary>
    /// <param name="find">Finds a game file's list of textures. Files it doesn't know are left out.</param>
    /// <param name="read">Reads a game file as it now stands: the mod's version if it has one.</param>
    /// <param name="encodePng">Turns a picture into PNG bytes. Only used for <see cref="TeamBundleFormat.Png"/>.</param>
    public static Result<TeamBundleExport> Export(
        string teamName,
        int artId,
        IEnumerable<TeamArt> art,
        Func<string, TextureFile?> find,
        Func<string, Result<byte[]>> read,
        DdsCodec codec,
        string parent,
        TeamBundleFormat format,
        Func<Rgba32Image, byte[]>? encodePng,
        Action<int, int>? progress = null,
        bool sortBySection = true)
    {
        if (format == TeamBundleFormat.Png && encodePng is null)
        {
            return Result.Failure<TeamBundleExport>("Nothing was given to write PNG files with.");
        }

        try
        {
            string folder = Path.Combine(parent, SafeName(teamName));
            Directory.CreateDirectory(folder);
            var manifest = new TeamBundleManifest { Team = teamName, ArtId = artId };
            var problems = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<(TeamArt Art, TextureFile File)> files = art
                .DistinctBy(a => a.Path)
                .Select(a => (Art: a, File: find(a.Path)))
                .Where(p => p.File is not null)
                .Select(p => (p.Art, p.File!))
                .ToList();

            int done = 0;
            foreach ((TeamArt item, TextureFile file) in files)
            {
                progress?.Invoke(++done, files.Count);
                Result<byte[]> data = read(item.Path);
                if (!data.IsSuccess)
                {
                    problems.Add($"{item.Label}: {data.Error}");
                    continue;
                }

                foreach (TextureInfo texture in file.Textures)
                {
                    Result<byte[]> picture = format == TeamBundleFormat.Dds
                        ? ToDds(data.Value!, item.Path, codec, texture.Index)
                        : ToPng(data.Value!, item.Path, codec, texture.Index, encodePng!);
                    if (!picture.IsSuccess)
                    {
                        problems.Add($"{item.Label}: {picture.Error}");
                        continue;
                    }

                    string relative = NameFor(item, file, texture, format == TeamBundleFormat.Dds ? ".dds" : ".png", used, sortBySection);
                    string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, picture.Value!);
                    manifest.Entries.Add(new TeamBundleEntry { GamePath = item.Path, TextureIndex = texture.Index, File = relative, Sha256 = Hash(picture.Value!) });
                }
            }

            File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));
            File.WriteAllText(Path.Combine(folder, "README.txt"), ReadMe(teamName), Encoding.UTF8);
            return Result.Success(new TeamBundleExport(folder, manifest.Entries.Count, problems));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<TeamBundleExport>($"The bundle wasn't saved: {ex.Message}", ex);
        }
    }

    /// <summary>Puts edited pictures from a bundle folder into game files.</summary>
    /// <param name="exists">True for a path the game really has. A bundle can't add or touch anything else.</param>
    /// <param name="read">Reads a game file as it now stands, so other edits to it are kept.</param>
    /// <param name="save">Saves a changed game file to the mod.</param>
    public static Result<TeamBundleImport> Import(
        string folder,
        Func<string, bool> exists,
        Func<string, Result<byte[]>> read,
        Func<string, byte[], Result<bool>> save,
        DdsCodec codec,
        Action<int, int>? progress = null)
    {
        TeamBundleManifest? manifest;
        try
        {
            string manifestPath = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifestPath))
            {
                return Result.Failure<TeamBundleImport>($"That folder has no {ManifestName}, so it isn't a bundle. Pick the folder the app made.");
            }

            manifest = JsonSerializer.Deserialize<TeamBundleManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result.Failure<TeamBundleImport>($"{ManifestName} couldn't be read: {ex.Message}", ex);
        }

        if (manifest is not { FormatVersion: 1 })
        {
            return Result.Failure<TeamBundleImport>("This bundle was made by a newer version of the app.");
        }

        var problems = new List<string>();
        string root = Path.GetFullPath(folder);
        var edited = new List<(TeamBundleEntry Entry, string Path, byte[] Bytes)>();
        foreach (TeamBundleEntry entry in manifest.Entries)
        {
            if (FindEdit(root, entry, problems) is { } found)
            {
                if (exists(entry.GamePath))
                {
                    edited.Add((entry, found.Path, found.Bytes));
                }
                else
                {
                    problems.Add($"{entry.File}: this game has no {entry.GamePath}.");
                }
            }
        }

        var changed = new List<string>();
        int pictures = 0;
        int done = 0;
        List<IGrouping<string, (TeamBundleEntry Entry, string Path, byte[] Bytes)>> groups = edited.GroupBy(e => e.Entry.GamePath).ToList();
        foreach (IGrouping<string, (TeamBundleEntry Entry, string Path, byte[] Bytes)> group in groups)
        {
            progress?.Invoke(++done, groups.Count);
            Result<byte[]> current = read(group.Key);
            if (!current.IsSuccess)
            {
                problems.Add($"{group.Key}: {current.Error}");
                continue;
            }

            byte[] file = current.Value!;
            int applied = 0;
            foreach ((TeamBundleEntry entry, _, byte[] bytes) in group)
            {
                Result<byte[]> swapped = Swap(file, entry.GamePath, bytes, codec, entry.TextureIndex);
                if (swapped.IsSuccess)
                {
                    file = swapped.Value!;
                    applied++;
                }
                else
                {
                    problems.Add($"{entry.File}: {swapped.Error}");
                }
            }

            if (applied > 0)
            {
                Result<bool> saved = save(group.Key, file);
                if (saved.IsSuccess)
                {
                    pictures += applied;
                    changed.Add(group.Key);
                }
                else
                {
                    problems.Add($"{group.Key}: {saved.Error}");
                }
            }
        }

        return Result.Success(new TeamBundleImport(manifest.Team, pictures, changed.Count, problems, changed));
    }

    /// <summary>The edited picture for an entry, if there is one. A picture saved under the other extension counts too.</summary>
    private static (string Path, byte[] Bytes)? FindEdit(string root, TeamBundleEntry entry, List<string> problems)
    {
        string path = Path.GetFullPath(Path.Combine(root, entry.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{entry.File}: that's outside the bundle folder, so it was left alone.");
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (!Hash(bytes).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return (path, bytes);
                }
            }

            string other = Path.ChangeExtension(path, Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) ? ".png" : ".dds");
            return File.Exists(other) ? (other, File.ReadAllBytes(other)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{entry.File}: {ex.Message}");
            return null;
        }
    }

    private static Result<byte[]> Swap(byte[] file, string gamePath, byte[] picture, DdsCodec codec, int textureIndex)
    {
        if (TextureDds.IsDds(picture))
        {
            return GameTextures.Import(file, gamePath, picture, codec, textureIndex);
        }

        Result<Rgba32Image> decoded = codec.DecodeImageFile(picture);
        return decoded.IsSuccess
            ? GameTextures.Replace(file, gamePath, decoded.Value!, codec, textureIndex)
            : Result.Failure<byte[]>("That file couldn't be opened as a picture.");
    }

    private static Result<byte[]> ToDds(byte[] file, string gamePath, DdsCodec codec, int textureIndex)
    {
        Result<ExportedDds> exported = GameTextures.Export(file, gamePath, codec, textureIndex);
        return exported.IsSuccess ? Result.Success(exported.Value!.Bytes) : Result.Failure<byte[]>(exported.Error!);
    }

    private static Result<byte[]> ToPng(byte[] file, string gamePath, DdsCodec codec, int textureIndex, Func<Rgba32Image, byte[]> encodePng)
    {
        Result<GameTexture> read = GameTextures.Read(file, gamePath, textureIndex, codec);
        if (!read.IsSuccess)
        {
            return Result.Failure<byte[]>(read.Error!);
        }

        GameTexture texture = read.Value!;
        Result<Rgba32Image> decoded = codec.Decode(texture.LinearPixelData, texture.Width, texture.Height, texture.Format);
        if (!decoded.IsSuccess)
        {
            return Result.Failure<byte[]>(decoded.Error!);
        }

        // Some textures use alpha for something else, which would otherwise save as an empty square.
        byte[] pixels = decoded.Value!.Pixels;
        bool empty = true;
        for (int i = 3; i < pixels.Length && empty; i += 4)
        {
            empty = pixels[i] == 0;
        }

        if (empty)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return Result.Success(encodePng(decoded.Value!));
    }

    private static string NameFor(TeamArt art, TextureFile file, TextureInfo texture, string extension, HashSet<string> used, bool sortBySection)
    {
        string section = art.Section switch
        {
            TeamSection.IceAndPuck => "Ice and puck",
            TeamSection.GoalieMasks => "Goalie masks",
            _ => art.Section.ToString(),
        };
        string role = file.Textures.Count > 1
            ? TextureCatalog.RoleFor(file.Path, texture.Index, file.Textures.Count) ?? $"{texture.Index + 1}"
            : string.Empty;
        string title = role.Length > 0 ? $"{art.Label} - {role}" : art.Label;
        string group = art.Group.Length > 0 ? SafeName(art.Group) + "/" : string.Empty;
        string baseName = (sortBySection ? section + "/" : string.Empty) + group + SafeName(title);
        string name = baseName + extension;
        if (!used.Add(name))
        {
            // Two files with the same label: the file's own name tells them apart.
            name = $"{baseName} ({SafeName(Path.GetFileNameWithoutExtension(file.Path))}){extension}";
            int count = 2;
            while (!used.Add(name))
            {
                name = $"{baseName} ({SafeName(Path.GetFileNameWithoutExtension(file.Path))} {count++}){extension}";
            }
        }

        return name;
    }

    private static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Untitled" : safe;
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string ReadMe(string team) =>
        $"""
        {team}: art from NHL Legacy Workbench

        Edit any picture here, keeping its name and folder, then choose Import in the app and pick this folder.
        Only pictures that changed go into your mod, so leave the rest as they are or delete them.

        You can save a .dds in place of a .png, or the other way round, with the same name.
        Don't change bundle.json. It says which game texture each picture belongs to.
        """;
}
