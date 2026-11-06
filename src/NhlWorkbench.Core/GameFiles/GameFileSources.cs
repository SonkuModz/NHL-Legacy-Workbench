using NhlWorkbench.Core.Formats.Iso;

namespace NhlWorkbench.Core.GameFiles;

/// <summary>A file visible in a source. The path is relative to the game's USRDIR.</summary>
public sealed record GameSourceFile(string RelativePath, long Length);

/// <summary>A read-only view of one USRDIR tree: a disc image, or a folder on disk.</summary>
public interface IGameFileSource
{
    string Description { get; }

    IReadOnlyList<GameSourceFile> Files { get; }

    /// <summary>Opens a seekable read-only stream. The caller owns it.</summary>
    Stream Open(string relativePath);
}

/// <summary>The USRDIR tree inside a PS3 disc image.</summary>
public sealed class IsoGameFileSource : IGameFileSource
{
    private const string UsrDirPrefix = "PS3_GAME/USRDIR/";

    private readonly Iso9660Image _image;

    public IsoGameFileSource(Iso9660Image image, string description)
    {
        _image = image;
        Description = description;
        Files = image.Files
            .Where(f => f.Path.StartsWith(UsrDirPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new GameSourceFile(f.Path[UsrDirPrefix.Length..], f.Length))
            .ToArray();
    }

    public string Description { get; }

    public IReadOnlyList<GameSourceFile> Files { get; }

    public Stream Open(string relativePath)
    {
        IsoFile file = _image.Find(UsrDirPrefix + relativePath)
            ?? throw new FileNotFoundException($"{relativePath} isn't on the disc image.");
        return _image.OpenFile(file);
    }
}

/// <summary>A USRDIR folder on disk. Shell leftovers are ignored, as the recomp launcher ignores them.</summary>
public sealed class DirectoryGameFileSource : IGameFileSource
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private readonly string _root;

    public DirectoryGameFileSource(string root, string description)
    {
        _root = root;
        Description = description;
        Files = new DirectoryInfo(root)
            .EnumerateFiles("*", Enumeration)
            .Where(f => !IsShellLeftover(f.Name))
            .Select(f => new GameSourceFile(Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length))
            .ToArray();
    }

    public string Description { get; }

    public IReadOnlyList<GameSourceFile> Files { get; }

    public Stream Open(string relativePath) =>
        new FileStream(Path.Combine(_root, relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static bool IsShellLeftover(string name) =>
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.Ordinal);
}
