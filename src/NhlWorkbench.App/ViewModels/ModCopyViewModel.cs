using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A file in an installed mod, in the list to copy from.</summary>
public sealed partial class ModCopyRow(InstalledModFile file, string what, bool isInMine, Action changed) : ObservableObject
{
    [ObservableProperty]
    private bool _isTicked;

    public InstalledModFile File { get; } = file;

    public string Path => File.GamePath;

    public string Size => Imaging.FormatSize(File.Size);

    public string What { get; } = what;

    public bool IsInMine { get; } = isInMine;

    partial void OnIsTickedChanged(bool value) => changed();
}

/// <summary>My Mod's panel for bringing files in from a mod the launcher has installed.</summary>
public partial class ModViewModel
{
    private List<ModCopyRow> _copyAll = [];
    private ListCollectionView? _copyView;
    private bool _bulkTicking;

    [ObservableProperty]
    private bool _isCopyOpen;

    [ObservableProperty]
    private string _copyTitle = string.Empty;

    [ObservableProperty]
    private string _copyQuery = string.Empty;

    [ObservableProperty]
    private ListCollectionView? _copyRows;

    [ObservableProperty]
    private string _copySummary = string.Empty;

    partial void OnCopyQueryChanged(string value)
    {
        _copyView?.Refresh();
        UpdateCopySummary();
    }

    [RelayCommand]
    private void OpenCopy(InstalledRow? row)
    {
        if (row is null || _main.InstallRoot is not { } install || !_main.EnsureProject())
        {
            return;
        }

        HashSet<string> mine = _main.Project!.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _copyAll = InstalledMods.FilesOf(install, row.Name)
            .Select(f => new ModCopyRow(f, Describe(f.GamePath), mine.Contains(f.GamePath), UpdateCopySummary))
            .ToList();
        _copyView = new ListCollectionView(_copyAll) { Filter = MatchesCopy };
        CopyRows = _copyView;
        CopyTitle = $"Copy from {row.Name}";
        CopyQuery = string.Empty;
        UpdateCopySummary();
        IsCopyOpen = true;
    }

    [RelayCommand]
    private void CloseCopy() => IsCopyOpen = false;

    [RelayCommand]
    private void TickShown(string how)
    {
        _bulkTicking = true;
        foreach (ModCopyRow row in _copyView?.Cast<ModCopyRow>() ?? [])
        {
            row.IsTicked = how == "yes";
        }

        _bulkTicking = false;
        UpdateCopySummary();
    }

    [RelayCommand]
    public async Task CopyTickedAsync()
    {
        List<ModCopyRow> ticked = _copyAll.Where(r => r.IsTicked).ToList();
        if (ticked.Count == 0 || _main.Project is not { } project)
        {
            return;
        }

        int replacing = ticked.Count(r => r.IsInMine);
        if (replacing > 0 && !_main.IsWalking
            && !Dialogs.Confirm("Replace your versions?", $"Your mod already has {replacing:N0} of these files, and copying replaces them. A snapshot first lets you go back.", "Replace them"))
        {
            return;
        }

        NhlWorkbench.Core.GameFiles.GameFileIndex? index = _main.Index;
        List<InstalledModFile> files = ticked.Select(r => r.File).ToList();
        _main.BusyText = "Copying files";
        (int copied, IReadOnlyList<string> problems) = await Task.Run(() =>
            InstalledMods.CopyInto(project, files, index, (done, total) => _main.BusyText = $"Copying files: {done:N0} of {total:N0}"));
        _main.BusyText = null;
        IsCopyOpen = false;
        await _main.FilesChangedAsync(files.Select(f => f.GamePath));

        if (problems.Count > 0 && !_main.IsWalking)
        {
            Dialogs.Tell($"{problems.Count:N0} file{(problems.Count == 1 ? " wasn't" : "s weren't")} copied", string.Join("\n", problems.Take(12)));
        }

        _main.Status = $"Copied {copied:N0} file{(copied == 1 ? string.Empty : "s")} into {project.Name}.";
    }

    private bool MatchesCopy(object item)
    {
        var row = (ModCopyRow)item;
        return CopyQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            row.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || row.What.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCopySummary()
    {
        if (_bulkTicking)
        {
            return;
        }

        int ticked = _copyAll.Count(r => r.IsTicked);
        long bytes = _copyAll.Where(r => r.IsTicked).Sum(r => r.File.Size);
        int shown = _copyView?.Count ?? 0;
        CopySummary = $"Showing {shown:N0} of {_copyAll.Count:N0} files. {ticked:N0} ticked" + (ticked > 0 ? $", {Imaging.FormatSize(bytes)}." : ".");
    }
}
