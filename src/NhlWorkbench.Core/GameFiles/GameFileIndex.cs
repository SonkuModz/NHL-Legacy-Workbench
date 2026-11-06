using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Eb;

namespace NhlWorkbench.Core.GameFiles;

public enum GameLayerKind
{
    Mod,
    Update,
    Disc,
}

/// <summary>One source of game files. A layer listed earlier beats one listed later.</summary>
public sealed record GameLayer(GameLayerKind Kind, string Name, IGameFileSource Source);

/// <summary>One copy of a game file: loose on disk, or a member of an archive.</summary>
/// <param name="Path">Normalized path, lowercase with forward slashes.</param>
/// <param name="OriginalPath">The path as the source spells it.</param>
/// <param name="Layer">The layer holding this copy.</param>
/// <param name="ArchivePath">Normalized path of the containing archive, or null for a loose file.</param>
/// <param name="StoredSize">Bytes on disk, before any decompression.</param>
public sealed record GameFileCopy(string Path, string OriginalPath, GameLayer Layer, string? ArchivePath, long StoredSize)
{
    public bool IsLoose => ArchivePath is null;

    internal EbArchive? Archive { get; init; }

    internal EbEntry? Entry { get; init; }

    internal int Rank { get; init; }
}

/// <summary>A game path and every copy of it, best first.</summary>
public sealed class GameFile
{
    internal GameFile(string path, IReadOnlyList<GameFileCopy> copies)
    {
        Path = path;
        Copies = copies;
    }

    public string Path { get; }

    public IReadOnlyList<GameFileCopy> Copies { get; }

    /// <summary>The copy expected to load in game.</summary>
    public GameFileCopy Winner => Copies[0];

    /// <summary>
    /// True when the winner is an archive member and another archive holds the same path. The recomp's
    /// logs show loose files being tried before archives, but nothing establishes the order between
    /// archives, including update patch.big against the disc archives. The winner is then a best guess.
    /// </summary>
    public bool WinnerIsUncertain => !Winner.IsLoose && Copies.Count(c => !c.IsLoose) > 1;
}

/// <summary>
/// Lists every game file across a disc, its update and installed mods, and which copy of each should
/// load. Nothing here writes to any source.
///
/// Priority, best first: loose files by layer (mods, update, disc), then archive members by layer.
/// Only the winning copy of an archive contributes members, because a higher layer's copy replaces it
/// at that path. Archives inside other archives are not expanded here; use <see cref="OpenNested"/>.
/// </summary>
public sealed class GameFileIndex : IDisposable
{
    /// <summary>Folders the game mounts archives from, relative to USRDIR.</summary>
    private static readonly HashSet<string> MountFolders = new(StringComparer.Ordinal) { string.Empty, "gamedata", "gamedata/anim" };

    private readonly List<EbArchive> _archives;
    private readonly Dictionary<string, GameFile> _byPath;

    private GameFileIndex(IReadOnlyList<GameLayer> layers, List<EbArchive> archives, IReadOnlyList<GameFile> files, IReadOnlyList<ParseWarning> warnings)
    {
        Layers = layers;
        _archives = archives;
        Files = files;
        Warnings = warnings;
        _byPath = new Dictionary<string, GameFile>(files.Count, StringComparer.Ordinal);
        foreach (GameFile file in files)
        {
            _byPath.Add(file.Path, file);
        }
    }

    public IReadOnlyList<GameLayer> Layers { get; }

    /// <summary>Every path, sorted.</summary>
    public IReadOnlyList<GameFile> Files { get; }

    public IReadOnlyList<ParseWarning> Warnings { get; }

    /// <summary>Paths of the archives that contributed members.</summary>
    public IEnumerable<string> MountedArchives => _archives.Select(a => a.SourceName);

    public static Result<GameFileIndex> Build(IReadOnlyList<GameLayer> layers)
    {
        var archives = new List<EbArchive>();
        try
        {
            var warnings = new List<ParseWarning>();
            var copies = new Dictionary<string, List<GameFileCopy>>(StringComparer.Ordinal);

            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                GameLayer layer = layers[layerIndex];
                foreach (GameSourceFile file in layer.Source.Files)
                {
                    string? path = NormalizePath(file.RelativePath);
                    if (path is null)
                    {
                        warnings.Add(new ParseWarning($"Skipped unsafe path {file.RelativePath}.", layer.Name));
                        continue;
                    }

                    Add(copies, new GameFileCopy(path, file.RelativePath, layer, null, file.Length) { Rank = layerIndex });
                }
            }

            // Snapshot first: adding members changes the dictionary.
            foreach (GameFileCopy winner in copies.Values.Select(list => list.MinBy(c => c.Rank)!).ToArray())
            {
                if (IsMountCandidate(winner.Path))
                {
                    Mount(winner, layers, archives, copies, warnings);
                }
            }

            GameFile[] files = copies
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new GameFile(pair.Key, pair.Value.OrderBy(c => c.Rank).ThenBy(c => c.ArchivePath, StringComparer.Ordinal).ToArray()))
                .ToArray();
            return Result.Success(new GameFileIndex(layers, archives, files, warnings), warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            archives.ForEach(a => a.Dispose());
            return Result.Failure<GameFileIndex>($"Can't read the game files: {ex.Message}", ex);
        }
    }

    /// <summary>Finds a path, ignoring case. Accepts backslashes and a leading "./".</summary>
    public GameFile? Find(string path)
    {
        string? normalized = NormalizePath(path);
        return normalized is null ? null : _byPath.GetValueOrDefault(normalized);
    }

    /// <summary>
    /// Finds paths containing every word in <paramref name="query"/>, ignoring case. Matches in the
    /// file name sort ahead of matches that need the folder path.
    /// </summary>
    public IReadOnlyList<GameFile> Search(string query, int limit = 200)
    {
        string[] words = query.ToLowerInvariant().Replace('\\', '/').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return Files
            .Where(f => words.All(f.Path.Contains))
            .OrderBy(f => words.All(FileName(f.Path).Contains) ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Reads a copy exactly as stored.</summary>
    public Result<byte[]> ReadStored(GameFileCopy copy)
    {
        if (copy.Archive is not null)
        {
            return copy.Archive.ReadStored(copy.Entry!);
        }

        try
        {
            using Stream stream = copy.Layer.Source.Open(copy.OriginalPath);
            if (stream.Length > Array.MaxLength)
            {
                return Result.Failure<byte[]>($"{copy.Path} is too large to read into memory ({stream.Length:N0} bytes).");
            }

            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Result.Success(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<byte[]>($"Can't read {copy.Path}: {ex.Message}", ex);
        }
    }

    /// <summary>Reads a copy and removes its chunkzip wrapper, if it has one.</summary>
    public Result<byte[]> ReadDecoded(GameFileCopy copy)
    {
        Result<byte[]> stored = ReadStored(copy);
        if (!stored.IsSuccess || !ChunkZip.IsChunkZip(stored.Value))
        {
            return stored;
        }

        Result<byte[]> decoded = ChunkZip.Decompress(stored.Value!);
        return decoded.IsSuccess ? decoded : Result.Failure<byte[]>($"{copy.Path}: {decoded.Error}", decoded.Exception);
    }

    /// <summary>Opens a copy as an archive of smaller files, such as a BIGF portrait bundle.</summary>
    public Result<NestedArchive> OpenNested(GameFileCopy copy)
    {
        Result<byte[]> decoded = ReadDecoded(copy);
        return decoded.IsSuccess
            ? NestedArchive.Open(decoded.Value!, copy.Path)
            : Result.Failure<NestedArchive>(decoded.Error!, decoded.Exception);
    }

    public void Dispose()
    {
        foreach (EbArchive archive in _archives)
        {
            archive.Dispose();
        }
    }

    /// <summary>Lowercase, forward slashes, no "." segments. Returns null for paths that could escape a folder.</summary>
    public static string? NormalizePath(string path)
    {
        var segments = new List<string>();
        foreach (string segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return null;
            }

            if (segment != ".")
            {
                segments.Add(segment.ToLowerInvariant());
            }
        }

        return segments.Count == 0 || segments[0].Contains(':') ? null : string.Join('/', segments);
    }

    private static void Mount(GameFileCopy archiveFile, IReadOnlyList<GameLayer> layers, List<EbArchive> archives, Dictionary<string, List<GameFileCopy>> copies, List<ParseWarning> warnings)
    {
        Stream stream = archiveFile.Layer.Source.Open(archiveFile.OriginalPath);
        var magic = new byte[4];
        if (stream.Length < 4 || stream.Read(magic) != 4 || !EbArchive.IsEbArchive(magic))
        {
            stream.Dispose();
            return;
        }

        Result<EbArchive> opened = EbArchive.Open(stream, archiveFile.Path);
        if (!opened.IsSuccess)
        {
            stream.Dispose();
            warnings.Add(new ParseWarning(opened.Error!, archiveFile.Layer.Name));
            return;
        }

        warnings.AddRange(opened.Warnings);
        EbArchive archive = opened.Value!;
        archives.Add(archive);

        // Every loose copy outranks every archive member.
        int rank = layers.Count + archiveFile.Rank;
        foreach (EbEntry entry in archive.Entries)
        {
            string? path = NormalizePath(entry.Path);
            if (path is null)
            {
                warnings.Add(new ParseWarning($"Skipped unsafe path {entry.Path}.", archiveFile.Path));
                continue;
            }

            Add(copies, new GameFileCopy(path, entry.Path, archiveFile.Layer, archiveFile.Path, entry.StoredSize)
            {
                Archive = archive,
                Entry = entry,
                Rank = rank,
            });
        }
    }

    private static bool IsMountCandidate(string path)
    {
        int slash = path.LastIndexOf('/');
        string folder = slash >= 0 ? path[..slash] : string.Empty;
        return path.EndsWith(".big", StringComparison.Ordinal) && MountFolders.Contains(folder);
    }

    private static void Add(Dictionary<string, List<GameFileCopy>> copies, GameFileCopy copy)
    {
        if (!copies.TryGetValue(copy.Path, out List<GameFileCopy>? list))
        {
            list = new List<GameFileCopy>(1);
            copies.Add(copy.Path, list);
        }

        list.Add(copy);
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
