using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

/// <summary>A picture waiting to go into a game file.</summary>
/// <param name="Label">What to call it in a message.</param>
/// <param name="ReadPicture">Reads it when it's needed, so a big folder isn't all held in memory.</param>
public sealed record PictureEdit(string GamePath, int TextureIndex, string Label, Func<byte[]> ReadPicture);

/// <summary>What a folder's pictures were matched to.</summary>
/// <param name="Edits">The pictures with a place to go.</param>
/// <param name="Problems">Pictures that couldn't be placed, and why.</param>
public sealed record PictureFolderMatch(IReadOnlyList<PictureEdit> Edits, IReadOnlyList<string> Problems);

/// <summary>
/// A folder of loose pictures, placed by file name: <c>jersey_0_15_0.png</c> replaces the texture of that name, the same
/// name Save PNG and Save DDS suggest. A file holding several textures takes <c>name_1</c>, <c>name_2</c> and so on.
/// </summary>
public static class PictureFolder
{
    private static readonly HashSet<string> PictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".dds", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };

    /// <summary>Matches every picture under a folder to the game's textures.</summary>
    public static Result<PictureFolderMatch> Match(string folder, IReadOnlyList<TextureFile> files)
    {
        try
        {
            ILookup<string, TextureFile> byName = files.ToLookup(f => Path.GetFileNameWithoutExtension(f.Path), StringComparer.OrdinalIgnoreCase);
            var edits = new List<PictureEdit>();
            var problems = new List<string>();
            var taken = new Dictionary<(string, int), string>();

            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!PictureTypes.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                string shown = Path.GetRelativePath(folder, path).Replace('\\', '/');
                string? parent = Path.GetDirectoryName(path) is { } directory ? Path.GetFileName(directory) : null;
                (TextureFile? file, int texture, string? problem) = Place(Path.GetFileNameWithoutExtension(path), parent, byName);
                if (file is null)
                {
                    problems.Add($"{shown}: {problem}");
                }
                else if (taken.TryGetValue((file.Path, texture), out string? first))
                {
                    problems.Add($"{shown}: {first} already goes in that spot, so this one was left out.");
                }
                else
                {
                    taken[(file.Path, texture)] = shown;
                    edits.Add(new PictureEdit(file.Path, texture, shown, () => File.ReadAllBytes(path)));
                }
            }

            return Result.Success(new PictureFolderMatch(edits, problems));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result.Failure<PictureFolderMatch>($"That folder couldn't be read: {ex.Message}", ex);
        }
    }

    /// <summary>Puts matched pictures into their game files, keeping other edits to those files.</summary>
    public static (int Pictures, IReadOnlyList<string> Changed, IReadOnlyList<string> Problems) Apply(
        IReadOnlyList<PictureEdit> edits,
        Func<string, Result<byte[]>> read,
        Func<string, byte[], Result<bool>> save,
        DdsCodec codec,
        Action<int, int>? progress = null)
    {
        var changed = new List<string>();
        var problems = new List<string>();
        int pictures = 0;
        int done = 0;
        List<IGrouping<string, PictureEdit>> groups = edits.GroupBy(e => e.GamePath).ToList();
        foreach (IGrouping<string, PictureEdit> group in groups)
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
            foreach (PictureEdit edit in group)
            {
                Result<byte[]> swapped = Swap(file, edit, codec);
                if (swapped.IsSuccess)
                {
                    file = swapped.Value!;
                    applied++;
                }
                else
                {
                    problems.Add($"{edit.Label}: {swapped.Error}");
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

        return (pictures, changed, problems);
    }

    private static Result<byte[]> Swap(byte[] file, PictureEdit edit, DdsCodec codec)
    {
        byte[] picture;
        try
        {
            picture = edit.ReadPicture();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<byte[]>(ex.Message, ex);
        }

        if (TextureDds.IsDds(picture))
        {
            return GameTextures.Import(file, edit.GamePath, picture, codec, edit.TextureIndex);
        }

        Result<Rgba32Image> decoded = codec.DecodeImageFile(picture);
        return decoded.IsSuccess
            ? GameTextures.Replace(file, edit.GamePath, decoded.Value!, codec, edit.TextureIndex)
            : Result.Failure<byte[]>("That file couldn't be opened as a picture.");
    }

    private static (TextureFile? File, int Texture, string? Problem) Place(string stem, string? parent, ILookup<string, TextureFile> byName)
    {
        // The whole name comes first, since game textures often end in _0 or _1 themselves.
        List<TextureFile> exact = byName[stem].ToList();
        if (exact.Count > 0)
        {
            (TextureFile? file, string? problem) = Choose(exact, stem, parent);
            return file is null ? (null, 0, problem)
                : file.Textures.Count > 1 ? (null, 0, $"{stem} holds {file.Textures.Count} textures. Name this {stem}_1 to {stem}_{file.Textures.Count} to say which.")
                : (file, 0, null);
        }

        int split = stem.LastIndexOf('_');
        if (split > 0 && int.TryParse(stem[(split + 1)..], out int number))
        {
            string name = stem[..split];
            List<TextureFile> several = byName[name].Where(f => f.Textures.Count > 1).ToList();
            if (several.Count > 0)
            {
                (TextureFile? file, string? problem) = Choose(several, name, parent);
                return file is null ? (null, 0, problem)
                    : number >= 1 && number <= file.Textures.Count ? (file, number - 1, null)
                    : (null, 0, $"{name} only holds {file.Textures.Count} textures.");
            }
        }

        return (null, 0, "the game has no texture with this name.");
    }

    /// <summary>Some names are used in more than one game folder. The picture's own folder can say which is meant.</summary>
    private static (TextureFile? File, string? Problem) Choose(List<TextureFile> found, string name, string? parent)
    {
        if (found.Count == 1)
        {
            return (found[0], null);
        }

        List<TextureFile> here = found.Where(f => f.Path.Split('/') is { Length: >= 2 } parts && parts[^2].Equals(parent, StringComparison.OrdinalIgnoreCase)).ToList();
        return here.Count == 1
            ? (here[0], null)
            : (null, $"{found.Count} game folders have a texture called {name}. Put this in a folder named after the one you mean: {string.Join(", ", found.Take(4).Select(f => f.Path.Split('/') is { Length: >= 2 } p ? p[^2] : f.Path))}.");
    }
}
