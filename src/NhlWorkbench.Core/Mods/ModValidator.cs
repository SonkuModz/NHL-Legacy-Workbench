using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Eb;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Mods;

public enum ModIssueSeverity
{
    /// <summary>Worth knowing; the mod can still be exported.</summary>
    Warning,

    /// <summary>The mod wouldn't work as intended, so export is refused.</summary>
    Error,
}

public enum ModIssueCode
{
    EmptyProject,
    MissingProjectFile,
    ChangedProjectFile,
    CodePatchUnsupported,
    DroppedByLauncherImport,
    NotAGameFile,
    ReplacesWholeArchive,
    GameFileChanged,
    TypeMismatch,
    MuchLargerThanOriginal,
    OtherModChangesFile,
    KnownDamagedFile,
    InvalidRoster,
    EmptyFile,
}

public sealed record ModIssue(ModIssueSeverity Severity, ModIssueCode Code, string? Path, string Message);

/// <summary>Checks a project against the PS3 recomp's mod rules, and against the game if an index is given.</summary>
public static class ModValidator
{
    /// <summary>
    /// Files the launcher's importer treats as the official update when path and size both match.
    /// It doesn't compare contents, so a same-size edit would be dropped without a message.
    /// </summary>
    private static readonly Dictionary<string, long> OfficialUpdateFileSizes = new(StringComparer.Ordinal)
    {
        ["eboot.bin"] = 36_212_528,
        ["options.viv"] = 284,
        ["patch.big"] = 60_596_976,
    };

    /// <summary>Damaged on the retail disc: see docs/2.0-plan.md.</summary>
    private const string DamagedOnDisc = "scenario/scenario_online.big";

    /// <summary>Above this many times the original's size, warn about load time and video memory.</summary>
    private const int LargeFileFactor = 4;

    public static IReadOnlyList<ModIssue> Check(ModProject project, GameFileIndex? index = null)
    {
        var issues = new List<ModIssue>();
        if (project.Files.All(f => f.Size == 0) && !project.HasRoster)
        {
            // The launcher's importer refuses a pack whose files are all zero bytes, so this covers those too.
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.EmptyProject, null, "This project has nothing to export yet."));
        }

        foreach (ProjectFile file in project.Files)
        {
            CheckFile(project, file, index, issues);
        }

        if (project.HasRoster)
        {
            CheckRoster(project, issues);
        }

        return issues;
    }

    private static void CheckFile(ModProject project, ProjectFile file, GameFileIndex? index, List<ModIssue> issues)
    {
        string stored = project.StoredPath(file.Path);
        if (!File.Exists(stored))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.MissingProjectFile, file.Path, "This file is missing from the project folder."));
            return;
        }

        if (ModProject.HashFile(stored) != file.Sha256)
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.ChangedProjectFile, file.Path, "This file changed outside the Workbench. Add it to the project again to accept the change."));
            return;
        }

        if (Path.GetFileName(file.Path) == "eboot.bin")
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.CodePatchUnsupported, file.Path, "The recomp compiles the game's code into NHLLegacy.exe, so a changed EBOOT.BIN has no effect on how it runs."));
        }
        else if (OfficialUpdateFileSizes.TryGetValue(file.Path, out long officialSize) && officialSize == file.Size)
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.DroppedByLauncherImport, file.Path, "This is the same size as the official update's copy, so the launcher's importer would silently drop it."));
        }
        else if (IsTreatedAsJunk(file.Path))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.DroppedByLauncherImport, file.Path, "The launcher's importer treats this name as system clutter and would silently drop it."));
        }

        if (file.Size == 0)
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.EmptyFile, file.Path, "This file is empty."));
        }

        if (file.Path == DamagedOnDisc)
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.KnownDamagedFile, file.Path, "The retail disc's copy of this file is damaged, so it can't be used as a starting point and its use in game is unknown."));
        }

        if (index is not null)
        {
            CheckAgainstGame(project, file, stored, index, issues);
        }
    }

    private static bool IsTreatedAsJunk(string path)
    {
        string name = Path.GetFileName(path);
        return name is "desktop.ini" or "thumbs.db" or ".ds_store"
            || name.StartsWith("._", StringComparison.Ordinal)
            || path.Split('/').Contains("__macosx");
    }

    private static void CheckAgainstGame(ModProject project, ProjectFile file, string stored, GameFileIndex index, List<ModIssue> issues)
    {
        GameFile? game = index.Find(file.Path);
        if (game is null)
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.NotAGameFile, file.Path, "The game has no file at this path, so it may never load."));
            return;
        }

        if (index.MountedArchives.Contains(file.Path, StringComparer.Ordinal))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.ReplacesWholeArchive, file.Path, "This replaces a whole game archive, including every file inside it."));
        }

        foreach (string otherMod in game.Copies.Where(c => c.Layer.Kind == GameLayerKind.Mod && !c.Layer.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Layer.Name).Distinct())
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.OtherModChangesFile, file.Path, $"\"{otherMod}\" also changes this file. Whichever is higher in the launcher's mod list wins."));
        }

        GameFileCopy? original = game.Copies.FirstOrDefault(c => c.Layer.Kind != GameLayerKind.Mod);
        if (original is null)
        {
            return;
        }

        if (index.MountedArchives.Contains(file.Path, StringComparer.Ordinal))
        {
            return; // too big to read just for these checks, and the warning above already covers it
        }

        Result<byte[]> decoded = index.ReadDecoded(original);
        if (!decoded.IsSuccess)
        {
            return;
        }

        if (file.Base?.Sha256 is { } recorded && recorded != ModProject.Hash(decoded.Value!))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.GameFileChanged, file.Path, "The game's copy changed since this file was added, so your edit may be based on an old version."));
        }

        if (HasDifferentType(stored, decoded.Value!))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.TypeMismatch, file.Path, "This doesn't start the same way as the game's copy, so it may be the wrong file type or still compressed."));
        }

        // A menu package only looks bigger because its new picture is stored without packing. It unpacks to the same size.
        if (decoded.Value!.Length > 0 && file.Size > decoded.Value.Length * (long)LargeFileFactor && !MenuArt.IsPackage(file.Path))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Warning, ModIssueCode.MuchLargerThanOriginal, file.Path, $"This is over {LargeFileFactor} times the size of the game's copy, which can add load time and video memory use."));
        }
    }

    private static bool HasDifferentType(string stored, byte[] original)
    {
        var head = new byte[4];
        using FileStream stream = File.OpenRead(stored);
        int read = stream.Read(head);
        return original.Length >= 4 && (read < 4 || !head.AsSpan().SequenceEqual(original.AsSpan(0, 4)));
    }

    private static void CheckRoster(ModProject project, List<ModIssue> issues)
    {
        byte[] data = File.ReadAllBytes(project.RosterPath);
        if (!TdbSaveContainer.LooksLikeContainer(data) || !data.AsSpan().StartsWith("PS3RosterFile"u8))
        {
            issues.Add(new ModIssue(ModIssueSeverity.Error, ModIssueCode.InvalidRoster, "roster/SYS-DATA", "This isn't a PS3 roster save."));
        }
    }
}
