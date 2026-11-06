using System.Globalization;
using System.IO.Compression;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Mods;

/// <summary>A saved copy of a mod at one moment.</summary>
public sealed record ModSnapshot(string Path, string Label, DateTime Created, long Size);

/// <summary>What going back to a snapshot did.</summary>
public sealed record ModRestore(ModProject Project, ModSnapshot SafetyCopy, IReadOnlyList<string> AffectedPaths);

/// <summary>
/// Saved copies of a mod, kept as zips in its <c>snapshots</c> folder. Exports only take the mod's own files, so these never
/// reach anyone it's shared with.
/// </summary>
public static class ModSnapshots
{
    private const string FolderName = "snapshots";
    private const string TimeFormat = "yyyy-MM-dd_HHmmss";

    public static string FolderFor(ModProject project) => Path.Combine(project.Folder, FolderName);

    /// <summary>The mod's snapshots, newest first.</summary>
    public static IReadOnlyList<ModSnapshot> List(ModProject project)
    {
        try
        {
            string folder = FolderFor(project);
            return !Directory.Exists(folder)
                ? []
                : Directory.EnumerateFiles(folder, "*.zip").Select(Describe).OrderByDescending(s => s.Created).ThenByDescending(s => s.Path, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Saves a copy of the mod as it now stands.</summary>
    public static Result<ModSnapshot> Create(ModProject project, string label)
    {
        try
        {
            string folder = FolderFor(project);
            Directory.CreateDirectory(folder);
            string clean = string.Concat((label.Trim().Length > 0 ? label.Trim() : "Snapshot").Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            string stamp = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, $"{stamp} {clean}.zip");
            for (int count = 2; File.Exists(path); count++)
            {
                path = Path.Combine(folder, $"{stamp} {clean} {count}.zip");
            }

            string partial = path + ".part";
            using (FileStream stream = File.Create(partial))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(project.Folder, ModProject.ManifestFileName), ModProject.ManifestFileName, CompressionLevel.Fastest);
                foreach (string top in new[] { "files", "roster" })
                {
                    string source = Path.Combine(project.Folder, top);
                    if (Directory.Exists(source))
                    {
                        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            zip.CreateEntryFromFile(file, Path.GetRelativePath(project.Folder, file).Replace('\\', '/'), CompressionLevel.Fastest);
                        }
                    }
                }
            }

            File.Move(partial, path);
            return Result.Success(Describe(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<ModSnapshot>($"The snapshot wasn't saved: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Puts the mod back as it was in a snapshot. A copy of how it stands now is saved first, and nothing is touched unless the
    /// whole snapshot could be unpacked.
    /// </summary>
    public static Result<ModRestore> Restore(ModProject project, ModSnapshot snapshot)
    {
        string unpacked = Path.Combine(FolderFor(project), ".restoring");
        try
        {
            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, true);
            }

            Directory.CreateDirectory(unpacked);
            string root = Path.GetFullPath(unpacked) + Path.DirectorySeparatorChar;
            using (ZipArchive zip = ZipFile.OpenRead(snapshot.Path))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(unpacked, entry.FullName));
                    bool expected = entry.FullName == ModProject.ManifestFileName || entry.FullName.StartsWith("files/", StringComparison.Ordinal) || entry.FullName.StartsWith("roster/", StringComparison.Ordinal);
                    if (!expected || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result.Failure<ModRestore>("That file isn't one of this app's snapshots.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }

            Result<ModProject> check = ModProject.Open(unpacked);
            if (!check.IsSuccess || !File.Exists(Path.Combine(unpacked, ModProject.ManifestFileName)))
            {
                return Result.Failure<ModRestore>($"That snapshot can't be opened: {check.Error}");
            }

            Result<ModSnapshot> safety = Create(project, "Before going back");
            if (!safety.IsSuccess)
            {
                return Result.Failure<ModRestore>(safety.Error!);
            }

            HashSet<string> affected = project.Files.Select(f => f.Path).Concat(check.Value!.Files.Select(f => f.Path)).ToHashSet(StringComparer.Ordinal);
            foreach (string top in new[] { "files", "roster" })
            {
                string current = Path.Combine(project.Folder, top);
                if (Directory.Exists(current))
                {
                    Directory.Delete(current, true);
                }

                string saved = Path.Combine(unpacked, top);
                if (Directory.Exists(saved))
                {
                    Directory.Move(saved, current);
                }
            }

            File.Copy(Path.Combine(unpacked, ModProject.ManifestFileName), Path.Combine(project.Folder, ModProject.ManifestFileName), overwrite: true);
            Result<ModProject> reopened = ModProject.Open(project.Folder);
            return reopened.IsSuccess
                ? Result.Success(new ModRestore(reopened.Value!, safety.Value!, affected.ToList()))
                : Result.Failure<ModRestore>($"The mod couldn't be opened afterwards: {reopened.Error} A copy from just before is at {safety.Value!.Path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<ModRestore>($"The mod wasn't changed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(unpacked))
                {
                    Directory.Delete(unpacked, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a leftover folder, and the next restore clears it.
            }
        }
    }

    public static Result<bool> Delete(ModSnapshot snapshot)
    {
        try
        {
            File.Delete(snapshot.Path);
            return Result.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>($"The snapshot wasn't deleted: {ex.Message}", ex);
        }
    }

    private static ModSnapshot Describe(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int space = name.IndexOf(' ');
        bool dated = space > 0 && DateTime.TryParseExact(name[..space], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        var info = new FileInfo(path);
        DateTime created = dated ? DateTime.ParseExact(name[..space], TimeFormat, CultureInfo.InvariantCulture) : info.LastWriteTime;
        return new ModSnapshot(path, dated ? name[(space + 1)..] : name, created, info.Length);
    }
}
