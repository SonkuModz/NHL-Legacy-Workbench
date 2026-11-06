using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

public sealed record ModExportResult(string Path, int FileCount, bool HasRoster, IReadOnlyList<ModIssue> Issues);

/// <summary>
/// Writes a project out in the layout the recomp launcher imports:
/// <code>
/// USRDIR\&lt;game path&gt;
/// roster\SYS-DATA, roster\ICON0.PNG (optional)
/// workbench-mod.json (ignored by the launcher)
/// </code>
/// Output is built beside the target and verified before it replaces anything, so a failed export
/// leaves the previous one intact.
/// </summary>
public static class ModExporter
{
    private const string MarkerFileName = "workbench-mod.json";

    /// <summary>Exports to a folder. An existing folder is only replaced if it's empty or a previous export.</summary>
    public static Result<ModExportResult> ExportFolder(ModProject project, string targetFolder, GameFileIndex? index = null)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder));
        Result<IReadOnlyList<ModIssue>> checkedProject = Prepare(project, full, Path.GetFileName(full), index);
        if (!checkedProject.IsSuccess)
        {
            return Result.Failure<ModExportResult>(checkedProject.Error!);
        }

        string staging = full + ".exporting";
        string previous = full + ".previous";
        try
        {
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, MarkerFileName)))
            {
                return Result.Failure<ModExportResult>($"{full} already has other files in it. Pick an empty folder or a previous export.");
            }

            DeleteFolder(staging);
            WriteTree(project, staging);

            DeleteFolder(previous);
            if (Directory.Exists(full))
            {
                Directory.Move(full, previous);
            }

            try
            {
                Directory.Move(staging, full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(previous))
                {
                    Directory.Move(previous, full);
                }

                throw;
            }

            DeleteFolder(previous);
            return Result.Success(new ModExportResult(full, project.Files.Count, project.HasRoster, checkedProject.Value!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDeleteFolder(staging);
            return Result.Failure<ModExportResult>($"Can't export to {full}: {ex.Message}", ex);
        }
    }

    /// <summary>Exports to a zip. The launcher names the installed mod after the zip file.</summary>
    public static Result<ModExportResult> ExportZip(ModProject project, string targetZip, GameFileIndex? index = null)
    {
        string full = Path.GetFullPath(targetZip);
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<ModExportResult>($"{targetZip} has to end in .zip, or the launcher won't open it as a mod pack.");
        }

        Result<IReadOnlyList<ModIssue>> checkedProject = Prepare(project, full, Path.GetFileNameWithoutExtension(full), index);
        if (!checkedProject.IsSuccess)
        {
            return Result.Failure<ModExportResult>(checkedProject.Error!);
        }

        string staging = full + ".exporting";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Delete(staging);
            using (FileStream stream = new(staging, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach ((string source, string entryPath) in EnumerateOutput(project))
                {
                    zip.CreateEntryFromFile(source, entryPath, CompressionLevel.Optimal);
                }

                ZipArchiveEntry marker = zip.CreateEntry(MarkerFileName);
                using Stream markerStream = marker.Open();
                markerStream.Write(BuildMarker(project));
            }

            VerifyZip(project, staging);
            File.Move(staging, full, overwrite: true);
            return Result.Success(new ModExportResult(full, project.Files.Count, project.HasRoster, checkedProject.Value!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(staging);
            return Result.Failure<ModExportResult>($"Can't export to {full}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is in a recomp folder that mustn't be written to directly. data\hdd0
    /// is hard-linked to the mods and update, and it's rebuilt before every Play.
    /// </summary>
    public static bool IsProtectedRecompFolder(string path)
    {
        string normalized = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";
        return normalized.Contains("/data/hdd0/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/data/update/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/ps3_game/usrdir/", StringComparison.OrdinalIgnoreCase);
    }

    private static Result<IReadOnlyList<ModIssue>> Prepare(ModProject project, string target, string installedName, GameFileIndex? index)
    {
        if (IsProtectedRecompFolder(target))
        {
            return Result.Failure<IReadOnlyList<ModIssue>>($"{target} is inside the game's own files. Export somewhere else, and install through the launcher.");
        }

        // The launcher names the mod after the export, so that name has to survive as a mod folder and settings key.
        if (ModNames.Clean(installedName) != installedName)
        {
            return Result.Failure<IReadOnlyList<ModIssue>>($"\"{installedName}\" can't be used as a mod name. Pick a different export name.");
        }

        IReadOnlyList<ModIssue> issues;
        try
        {
            issues = ModValidator.Check(project, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<IReadOnlyList<ModIssue>>($"Can't check the project: {ex.Message}", ex);
        }

        ModIssue[] errors = issues.Where(i => i.Severity == ModIssueSeverity.Error).ToArray();
        return errors.Length > 0
            ? Result.Failure<IReadOnlyList<ModIssue>>("Fix these before exporting: " + string.Join(" ", errors.Select(e => e.Path is null ? e.Message : $"{e.Path}: {e.Message}")))
            : Result.Success(issues);
    }

    private static void WriteTree(ModProject project, string root)
    {
        foreach ((string source, string entryPath) in EnumerateOutput(project))
        {
            string destination = Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        File.WriteAllBytes(Path.Combine(root, MarkerFileName), BuildMarker(project));

        foreach (ProjectFile file in project.Files)
        {
            string written = Path.Combine(root, "USRDIR", file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (ModProject.HashFile(written) != file.Sha256)
            {
                throw new InvalidDataException($"{file.Path} didn't export correctly.");
            }
        }
    }

    private static void VerifyZip(ModProject project, string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (ProjectFile file in project.Files)
        {
            ZipArchiveEntry entry = zip.GetEntry("USRDIR/" + file.Path) ?? throw new InvalidDataException($"{file.Path} is missing from the zip.");
            using Stream stream = entry.Open();
            using var hash = System.Security.Cryptography.SHA256.Create();
            if (Convert.ToHexStringLower(hash.ComputeHash(stream)) != file.Sha256)
            {
                throw new InvalidDataException($"{file.Path} didn't export correctly.");
            }
        }
    }

    private static IEnumerable<(string Source, string EntryPath)> EnumerateOutput(ModProject project)
    {
        foreach (ProjectFile file in project.Files)
        {
            yield return (project.StoredPath(file.Path), "USRDIR/" + file.Path);
        }

        if (project.HasRoster)
        {
            yield return (project.RosterPath, "roster/SYS-DATA");
            if (File.Exists(project.RosterIconPath))
            {
                yield return (project.RosterIconPath, "roster/ICON0.PNG");
            }
        }
    }

    private static byte[] BuildMarker(ModProject project) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new { project.Name, FormatVersion = ModProject.CurrentFormatVersion, Target = "PS3 recomp BLUS31540", Files = project.Files.Select(f => new { f.Path, f.Size, f.Sha256 }) },
            new JsonSerializerOptions { WriteIndented = true });

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            DeleteFolder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging leftovers are harmless, and replaced by the next export.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging leftovers are harmless, and replaced by the next export.
        }
    }
}

/// <summary>The outcome of asking the recomp launcher to install a mod.</summary>
public sealed record ModInstallResult(bool Succeeded, string Message);

/// <summary>Installs an exported mod through the recomp launcher, so mods\ and its settings stay the launcher's own.</summary>
public static class RecompLauncher
{
    public const string LauncherExeName = "NHLLauncher.exe";

    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The launcher reads arguments in order, so --install has to come before --import-mod.</summary>
    /// A trailing slash on the mod's path would make it install under the launcher's fallback name, "Mod".
    public static IReadOnlyList<string> BuildImportArguments(string installDir, string modPath) =>
        ["--install", Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)), "--import-mod", Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath))];

    /// <summary>The name the launcher will give an exported folder or zip.</summary>
    public static string InstalledNameFor(string modPath)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath));
        return Directory.Exists(full) ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
    }

    /// <summary>
    /// True if importing would replace an installed mod of the same name, which the launcher does without
    /// asking. That mod could be someone else's, so ask the user first. A replaced mod keeps its place and
    /// its on/off state, so a disabled one stays disabled.
    /// </summary>
    public static bool WouldReplaceInstalledMod(string installDir, string modPath) =>
        Directory.Exists(Path.Combine(installDir, "mods", InstalledNameFor(modPath)));

    /// <summary>
    /// Runs the launcher without a window. It takes effect at the next Play. Only call this from an
    /// explicit user action, because it changes the install's mods folder and settings.
    /// </summary>
    public static Result<ModInstallResult> Import(string installDir, string modPath) =>
        Import(installDir, installDir, modPath, RunProcess);

    /// <summary>Uses the launcher in one folder to import into another, such as a throwaway test install.</summary>
    public static Result<ModInstallResult> Import(string launcherDir, string installDir, string modPath) =>
        Import(launcherDir, installDir, modPath, RunProcess);

    /// <summary>The same, with the process launch supplied by the caller. It returns the exit code.</summary>
    internal static Result<ModInstallResult> Import(string installDir, string modPath, Func<ProcessStartInfo, int?> run) =>
        Import(installDir, installDir, modPath, run);

    private static Result<ModInstallResult> Import(string launcherDir, string installDir, string modPath, Func<ProcessStartInfo, int?> run)
    {
        string launcher = Path.Combine(launcherDir, LauncherExeName);
        if (!File.Exists(launcher))
        {
            return Result.Failure<ModInstallResult>($"{launcherDir} has no {LauncherExeName}.");
        }

        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = installDir };
        foreach (string argument in BuildImportArguments(installDir, modPath))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            // An old result mustn't be mistaken for this one.
            string logPath = Path.Combine(installDir, "logs", "mod_import.log");
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            int? exitCode = run(start);
            if (exitCode is null)
            {
                return Result.Failure<ModInstallResult>("The launcher took too long to import the mod.");
            }

            string message = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "The launcher didn't report a result.";
            return Result.Success(new ModInstallResult(exitCode == 0, message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<ModInstallResult>($"Can't run the launcher: {ex.Message}", ex);
        }
    }

    private static int? RunProcess(ProcessStartInfo start)
    {
        using Process process = Process.Start(start) ?? throw new IOException("the launcher didn't start");
        if (process.WaitForExit(ImportTimeout))
        {
            return process.ExitCode;
        }

        process.Kill();
        return null;
    }
}
