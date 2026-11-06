using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

/// <summary>What a project file replaced when it was added, so a later change to the game can be noticed.</summary>
/// <param name="Layer">Name of the layer that held the original.</param>
/// <param name="ArchivePath">The archive holding the original, or null if it was loose.</param>
/// <param name="Size">Decoded size, or stored size when the original was too big to hash.</param>
/// <param name="Sha256">Hash of the decoded original, or null when it was too big to hash.</param>
public sealed record ProjectFileBase(string Layer, string? ArchivePath, long Size, string? Sha256);

/// <summary>One replaced game file.</summary>
/// <param name="Path">Normalized game path relative to USRDIR.</param>
public sealed record ProjectFile(string Path, long Size, string Sha256, ProjectFileBase? Base);

/// <summary>
/// A mod being worked on, kept in its own folder. Replacement files live under <c>files\</c> and the
/// game install is never written:
/// <code>
/// project.json
/// files\rendering\icesurface\centerlogo_0_cm.rpsgl
/// roster\SYS-DATA, roster\ICON0.PNG (optional)
/// </code>
/// </summary>
public sealed class ModProject
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestFileName = "project.json";

    /// <summary>Above this, the original is compared by size only. It covers whole archives.</summary>
    private const long MaxHashedBaseSize = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SortedDictionary<string, ProjectFile> _files;

    private ModProject(string folder, string name, IEnumerable<ProjectFile> files)
    {
        Folder = folder;
        Name = name;
        _files = new SortedDictionary<string, ProjectFile>(files.ToDictionary(f => f.Path), StringComparer.Ordinal);
    }

    public string Folder { get; }

    public string Name { get; }

    /// <summary>A snapshot, so background work can read it while the user edits.</summary>
    public IReadOnlyCollection<ProjectFile> Files
    {
        get
        {
            lock (_files)
            {
                return _files.Values.ToArray();
            }
        }
    }

    public bool HasFile(string normalizedPath)
    {
        lock (_files)
        {
            return _files.ContainsKey(normalizedPath);
        }
    }

    public bool HasRoster => File.Exists(RosterPath);

    public string RosterPath => Path.Combine(Folder, "roster", "SYS-DATA");

    public string RosterIconPath => Path.Combine(Folder, "roster", "ICON0.PNG");

    /// <summary>The roster as it was first added, for showing what has changed. It's never exported.</summary>
    public string RosterOriginalPath => Path.Combine(Folder, "roster", "SYS-DATA.original");

    /// <summary>Starts a project in a new or empty folder.</summary>
    public static Result<ModProject> Create(string folder, string name)
    {
        string? cleanName = ModNames.Clean(name);
        if (cleanName is null)
        {
            return Result.Failure<ModProject>($"\"{name}\" can't be used as a mod name.");
        }

        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return Result.Failure<ModProject>($"{folder} isn't empty. Pick an empty folder for a new project.");
            }

            Directory.CreateDirectory(folder);
            var project = new ModProject(folder, cleanName, []);
            project.SaveManifest();
            return Result.Success(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<ModProject>($"Can't create a project in {folder}: {ex.Message}", ex);
        }
    }

    /// <summary>Opens a project and checks that its files are all there and unchanged.</summary>
    public static Result<ModProject> Open(string folder)
    {
        try
        {
            string manifestPath = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return Result.Failure<ModProject>($"{folder} isn't a mod project (no {ManifestFileName}).");
            }

            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), JsonOptions);
            if (manifest is null)
            {
                return Result.Failure<ModProject>($"{manifestPath} is empty.");
            }

            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                return Result.Failure<ModProject>($"This project was made by a newer version of the Workbench (format {manifest.FormatVersion}).");
            }

            string? name = ModNames.Clean(manifest.Name ?? string.Empty);
            if (name is null)
            {
                return Result.Failure<ModProject>($"{manifestPath} has an unusable mod name.");
            }

            var warnings = new List<ParseWarning>();
            var files = manifest.Files ?? [];
            foreach (ProjectFile file in files)
            {
                if (file?.Path is null || file.Sha256 is null)
                {
                    return Result.Failure<ModProject>($"{manifestPath} has an incomplete file record.");
                }

                if (ModNames.CheckGamePath(file.Path) is not null || GameFileIndex.NormalizePath(file.Path) != file.Path)
                {
                    return Result.Failure<ModProject>($"{manifestPath} lists an unsafe path: {file.Path}.");
                }

                string stored = StoredPath(folder, file.Path);
                if (!File.Exists(stored))
                {
                    warnings.Add(new ParseWarning($"{file.Path} is missing from the project folder.", name));
                }
                else if (HashFile(stored) != file.Sha256)
                {
                    warnings.Add(new ParseWarning($"{file.Path} changed outside the Workbench.", name));
                }
            }

            if (files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != files.Length)
            {
                return Result.Failure<ModProject>($"{manifestPath} lists the same path twice.");
            }

            return Result.Success(new ModProject(folder, name, files), warnings);
        }
        catch (JsonException ex)
        {
            return Result.Failure<ModProject>($"{folder}'s {ManifestFileName} can't be read: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<ModProject>($"Can't open {folder}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Adds or replaces a file. <paramref name="content"/> is the plain decoded file, not a chunkzip
    /// payload. Passing the index records what's being replaced, for later checks. Replacing a file that
    /// is already in the project keeps its original record.
    /// </summary>
    public Result<ProjectFile> SetFile(string gamePath, byte[] content, GameFileIndex? index = null)
    {
        string? normalized = GameFileIndex.NormalizePath(gamePath);
        string? problem = normalized is null ? "It could point outside the game folder." : ModNames.CheckGamePath(normalized);
        if (problem is not null)
        {
            return Result.Failure<ProjectFile>($"{gamePath} can't be used. {problem}");
        }

        try
        {
            ProjectFile? existing;
            lock (_files)
            {
                _files.TryGetValue(normalized!, out existing);
            }

            ProjectFileBase? fileBase = existing?.Base ?? DescribeBase(normalized!, index);
            var file = new ProjectFile(normalized!, content.Length, Hash(content), fileBase);

            lock (_files)
            {
                WriteAtomic(StoredPath(Folder, file.Path), content);
                _files[file.Path] = file;
                SaveManifest();
            }

            return Result.Success(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<ProjectFile>($"Can't save {gamePath} to the project: {ex.Message}", ex);
        }
    }

    public Result<byte[]> ReadFile(string gamePath)
    {
        string? normalized = GameFileIndex.NormalizePath(gamePath);
        if (normalized is null || !HasFile(normalized))
        {
            return Result.Failure<byte[]>($"{gamePath} isn't in this project.");
        }

        try
        {
            lock (_files)
            {
                return Result.Success(File.ReadAllBytes(StoredPath(Folder, normalized)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<byte[]>($"Can't read {gamePath}: {ex.Message}", ex);
        }
    }

    /// <summary>Takes a file out of the project, so the game's own copy applies again.</summary>
    public Result<bool> RemoveFile(string gamePath)
    {
        string? normalized = GameFileIndex.NormalizePath(gamePath);
        if (normalized is null)
        {
            return Result.Success(false);
        }

        try
        {
            lock (_files)
            {
                if (!_files.Remove(normalized))
                {
                    return Result.Success(false);
                }

                SaveManifest();
                File.Delete(StoredPath(Folder, normalized));
            }

            return Result.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>($"Can't remove {gamePath}: {ex.Message}", ex);
        }
    }

    /// <summary>Sets the roster save this mod carries. An icon is optional.</summary>
    public Result<bool> SetRoster(byte[] sysData, byte[]? icon = null)
    {
        try
        {
            WriteAtomic(RosterPath, sysData);
            WriteAtomic(RosterOriginalPath, sysData);
            if (icon is not null)
            {
                WriteAtomic(RosterIconPath, icon);
            }
            else if (File.Exists(RosterIconPath))
            {
                File.Delete(RosterIconPath);
            }

            return Result.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>($"Can't save the roster: {ex.Message}", ex);
        }
    }

    /// <summary>Replaces the roster save after an edit, leaving its icon alone.</summary>
    public Result<bool> UpdateRoster(byte[] sysData)
    {
        try
        {
            WriteAtomic(RosterPath, sysData);
            return Result.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>($"Can't save the roster: {ex.Message}", ex);
        }
    }

    /// <summary>The original roster to compare with. A mod made before these were kept starts from what it has now.</summary>
    public Result<byte[]> ReadRosterOriginal()
    {
        try
        {
            if (!File.Exists(RosterOriginalPath))
            {
                File.Copy(RosterPath, RosterOriginalPath);
            }

            return Result.Success(File.ReadAllBytes(RosterOriginalPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<byte[]>($"Can't read the original roster: {ex.Message}", ex);
        }
    }

    public Result<bool> RemoveRoster()
    {
        try
        {
            string rosterFolder = Path.GetDirectoryName(RosterPath)!;
            if (Directory.Exists(rosterFolder))
            {
                File.Delete(RosterPath);
                File.Delete(RosterIconPath);
                File.Delete(RosterOriginalPath);
            }

            return Result.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>($"Can't remove the roster: {ex.Message}", ex);
        }
    }

    internal string StoredPath(string gamePath) => StoredPath(Folder, gamePath);

    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Describes the game's own copy, skipping mods, including an installed copy of this mod.</summary>
    internal static ProjectFileBase? DescribeBase(string path, GameFileIndex? index)
    {
        GameFileCopy? original = index?.Find(path)?.Copies.FirstOrDefault(c => c.Layer.Kind != GameLayerKind.Mod);
        if (original is null)
        {
            return null;
        }

        if (original.StoredSize > MaxHashedBaseSize)
        {
            return new ProjectFileBase(original.Layer.Name, original.ArchivePath, original.StoredSize, null);
        }

        // A damaged original shouldn't stop someone replacing it. It's only compared by size later.
        Result<byte[]> decoded = index!.ReadDecoded(original);
        if (!decoded.IsSuccess)
        {
            return new ProjectFileBase(original.Layer.Name, original.ArchivePath, original.StoredSize, null);
        }

        return new ProjectFileBase(original.Layer.Name, original.ArchivePath, decoded.Value!.Length, Hash(decoded.Value));
    }

    private static string StoredPath(string folder, string gamePath) =>
        Path.Combine(folder, "files", gamePath.Replace('/', Path.DirectorySeparatorChar));

    private void SaveManifest()
    {
        var manifest = new Manifest { FormatVersion = CurrentFormatVersion, Name = Name, Files = _files.Values.ToArray() };
        WriteAtomic(Path.Combine(Folder, ManifestFileName), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
    }

    private sealed class Manifest
    {
        public int FormatVersion { get; set; }

        public string? Name { get; set; }

        public ProjectFile[]? Files { get; set; }
    }
}
