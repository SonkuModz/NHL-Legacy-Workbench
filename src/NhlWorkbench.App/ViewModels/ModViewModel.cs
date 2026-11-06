using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A changed file. <paramref name="What"/> says in words what it is, when that's known.</summary>
public sealed record ModFileRow(string Path, string Size, string What, bool IsRoster = false)
{
    public bool HasWhat => What.Length > 0;
}

/// <summary>A mod in the game's launcher, as a line on this page.</summary>
public sealed record InstalledRow(string Name, string Facts, string Clash, bool HasClash, bool IsOff, bool CanCopy);

public sealed record SnapshotRow(ModSnapshot Snapshot, string Title, string Facts);

public sealed record IssueRow(bool IsError, string Where, string Message)
{
    public string Label => IsError ? "MUST FIX" : "HEADS UP";
}

public partial class ModViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private IReadOnlyList<ModFileRow> _files = [];

    [ObservableProperty]
    private IReadOnlyList<IssueRow> _issues = [];

    [ObservableProperty]
    private bool _hasIssues;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<InstalledRow> _installed = [];

    [ObservableProperty]
    private bool _hasInstalled;

    [ObservableProperty]
    private IReadOnlyList<SnapshotRow> _snapshots = [];

    [ObservableProperty]
    private bool _hasSnapshots;

    public ModViewModel(MainViewModel main)
    {
        _main = main;
    }

    public MainViewModel Main => _main;

    public void Update(ModProject? project, IReadOnlyList<ModIssue> issues)
    {
        Folder = project?.Folder ?? string.Empty;
        var files = project?.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new ModFileRow(f.Path, Imaging.FormatSize(f.Size), Describe(f.Path))).ToList() ?? [];
        if (project is { HasRoster: true } withRoster)
        {
            files.Insert(0, new ModFileRow("roster/SYS-DATA", Imaging.FormatSize(new FileInfo(withRoster.RosterPath).Length), "Roster save", IsRoster: true));
        }

        Files = files;
        RefreshInstalled(project);
        RefreshSnapshots();
        Issues = issues
            .OrderByDescending(i => i.Severity)
            .Select(i => new IssueRow(i.Severity == ModIssueSeverity.Error, i.Path ?? "This mod", i.Message))
            .ToList();
        HasIssues = Issues.Count > 0;
    }

    /// <summary>Says what a file is in words: whose art, or what kind of game data.</summary>
    private string Describe(string path)
    {
        IReadOnlyList<NhlWorkbench.Core.GameFiles.TeamArtOwner>? owners = _main.TeamCatalog?.OwnersOf(path);
        if (owners is { Count: > 0 })
        {
            string more = owners.Count > 1 ? $" and {owners.Count - 1} more" : string.Empty;
            return $"{owners[0].Team.Name}{more} · {owners[0].Art.Label}";
        }

        return path switch
        {
            _ when path.StartsWith("audio/", StringComparison.Ordinal) => "Sounds",
            _ when path.StartsWith("fe/loc/", StringComparison.Ordinal) => "Game text",
            _ when path.StartsWith("db/", StringComparison.Ordinal) => "Game database",
            _ when path.StartsWith("rendering/icesurface/", StringComparison.Ordinal) => "Ice",
            _ => string.Empty,
        };
    }

    private void RefreshInstalled(ModProject? project)
    {
        if (_main.InstallRoot is not { } install)
        {
            Installed = [];
            HasInstalled = false;
            return;
        }

        IReadOnlyList<InstalledMod> mods = InstalledMods.Read(install, project);
        Installed = mods.Select(mod =>
        {
            var facts = new List<string> { mod.FileCount == 1 ? "1 file" : $"{mod.FileCount:N0} files" };
            if (mod.IsMine)
            {
                facts.Add("this mod, as last installed");
            }

            if (mod.HasRoster)
            {
                facts.Add(mod.IsRosterInUse ? "its roster is in use" : "has a roster");
            }

            if (!mod.IsEnabled)
            {
                facts.Add("turned off");
            }

            string clash = mod.Shared.Count == 0
                ? string.Empty
                : $"Changes {(mod.Shared.Count == 1 ? "1 file" : $"{mod.Shared.Count:N0} files")} yours also changes. {(InstalledMods.MineWinsOver(mod, mods) ? "Yours wins." : "It wins, because it's higher in the launcher's list.")}";
            return new InstalledRow(mod.Name, string.Join(" · ", facts), clash, clash.Length > 0, !mod.IsEnabled, !mod.IsMine && mod.FileCount > 0);
        }).ToList();
        HasInstalled = Installed.Count > 0;
    }

    private void RefreshSnapshots()
    {
        Snapshots = _main.Project is { } project
            ? ModSnapshots.List(project).Select(s => new SnapshotRow(s, s.Label, $"{s.Created:d MMM yyyy, HH:mm} · {Imaging.FormatSize(s.Size)}")).ToList()
            : [];
        HasSnapshots = Snapshots.Count > 0;
    }

    [RelayCommand]
    private async Task TakeSnapshotAsync()
    {
        string? label = Dialogs.Ask("Save a snapshot", "This keeps a copy of the mod as it is now, to come back to. It stays in the mod's folder and isn't shared when you export.", "Save it", "Snapshot");
        if (label is not null)
        {
            await TakeSnapshotNamedAsync(label);
        }
    }

    /// <summary>Saves a snapshot without asking for its name.</summary>
    public async Task TakeSnapshotNamedAsync(string label)
    {
        if (_main.Project is not { } project)
        {
            return;
        }

        _main.BusyText = "Saving a snapshot";
        Result<ModSnapshot> saved = await Task.Run(() => ModSnapshots.Create(project, label));
        _main.BusyText = null;
        _main.Status = saved.IsSuccess ? $"Saved the snapshot \"{saved.Value!.Label}\"." : saved.Error!;
        RefreshSnapshots();
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync(SnapshotRow? row)
    {
        if (row is not null && Dialogs.Confirm("Go back to this snapshot?", $"{_main.Project?.Name} goes back to how it was at \"{row.Title}\". A copy of how it is now is saved as a new snapshot first.", "Go back"))
        {
            await RestoreAsync(row.Snapshot);
        }
    }

    /// <summary>Goes back to a snapshot without asking first.</summary>
    public async Task RestoreAsync(ModSnapshot snapshot)
    {
        if (_main.Project is not { } project)
        {
            return;
        }

        _main.BusyText = "Going back to the snapshot";
        Result<ModRestore> restored = await Task.Run(() => ModSnapshots.Restore(project, snapshot));
        _main.BusyText = null;
        if (!restored.IsSuccess)
        {
            Dialogs.Tell("The mod didn't change", restored.Error!);
            return;
        }

        await _main.ProjectRestoredAsync(restored.Value!);
        _main.Status = $"Back to \"{snapshot.Label}\". How it was a moment ago is saved as a snapshot too.";
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotRow? row)
    {
        if (row is not null && Dialogs.Confirm("Delete this snapshot?", $"\"{row.Title}\" goes for good. The mod itself doesn't change.", "Delete it"))
        {
            Result<bool> deleted = ModSnapshots.Delete(row.Snapshot);
            _main.Status = deleted.IsSuccess ? $"Deleted \"{row.Title}\"." : deleted.Error!;
            RefreshSnapshots();
        }
    }

    [RelayCommand]
    private async Task RemoveFileAsync(ModFileRow? row)
    {
        ModProject? project = _main.Project;
        if (row is null || project is null)
        {
            return;
        }

        if (row.IsRoster)
        {
            if (Dialogs.Confirm("Remove the roster from your mod?", $"This deletes the roster in {project.Name}, along with every edit made to it. Rosters in the game and in other mods aren't touched.", "Remove it"))
            {
                Result<bool> gone = project.RemoveRoster();
                _main.Status = gone.IsSuccess ? "The roster is out of your mod." : gone.Error!;
                _main.Roster.FileChangedElsewhere();
                await _main.RefreshProjectAsync();
            }

            return;
        }

        Result<bool> removed = project.RemoveFile(row.Path);
        _main.Status = removed.IsSuccess ? $"{row.Path} is back to the game's original." : removed.Error!;
        IceItem? item = _main.Ice.FindItem(row.Path);
        if (item is not null)
        {
            await _main.Ice.AfterEditAsync(item);
        }
        else
        {
            await _main.RefreshProjectAsync();
            _main.Textures.FileChanged(row.Path);
            _main.Text.FileChangedElsewhere();
        }
    }
}
