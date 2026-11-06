using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A mod folder used lately.</summary>
public sealed record RecentMod(string Name, string Folder);

public partial class MainViewModel : ObservableObject
{
    private readonly bool _rememberChoices;
    private readonly AppSettings _settings;
    private string? _connectedPath;
    private string? _connectedMods;
    private bool _following;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentMods))]
    private IReadOnlyList<RecentMod> _recentMods = [];

    public bool HasRecentMods => RecentMods.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupPage), nameof(IsRinkPage), nameof(IsIcePage), nameof(IsTeamsPage), nameof(IsUniformsPage), nameof(IsTeamsRailActive), nameof(IsRosterPage), nameof(IsDatabasePage), nameof(IsSoundsPage), nameof(IsVideosPage), nameof(IsTextPage), nameof(IsTexturesPage), nameof(IsFilesPage), nameof(IsModPage))]
    private object _currentPage;

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private string? _busyText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(CanInstall), nameof(GameTitle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(OpenLauncherCommand))]
    private GameFileIndex? _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(GameTitle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(OpenLauncherCommand))]
    private string? _installRoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(ModTitle), nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ExportFolderCommand), nameof(ExportZipCommand))]
    private ModProject? _project;

    [ObservableProperty]
    private int _editCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    public MainViewModel(bool rememberChoices = true)
    {
        _rememberChoices = rememberChoices;
        _settings = rememberChoices ? AppSettings.Load() : new AppSettings();
        Ice = new IceViewModel(this);
        Textures = new TexturesViewModel(this);
        Teams = new TeamsViewModel(this);
        Roster = new RosterViewModel(this);
        Database = new DatabaseViewModel(this);
        Sounds = new SoundsViewModel(this);
        Videos = new VideosViewModel(this);
        Text = new GameTextViewModel(this);
        QuickSearch = new QuickSearchViewModel(this);
        Files = new FilesViewModel(this);
        Mod = new ModViewModel(this);
        Setup = new SetupViewModel(this);
        _currentPage = this;
    }

    public IceViewModel Ice { get; }

    public TexturesViewModel Textures { get; }

    public TeamsViewModel Teams { get; }

    public RosterViewModel Roster { get; }

    public DatabaseViewModel Database { get; }

    public SoundsViewModel Sounds { get; }

    public VideosViewModel Videos { get; }

    public GameTextViewModel Text { get; }

    public QuickSearchViewModel QuickSearch { get; }

    /// <summary>True during the --shots walk, which nobody is there to answer dialogs for.</summary>
    public bool IsWalking { get; set; }

    /// <summary>The ffmpeg that comes with the game, which is how sounds and videos get decoded. Null without an install.</summary>
    public string? FfmpegPath => InstallRoot is { } root && File.Exists(Path.Combine(root, "ffmpeg", "ffmpeg.exe")) ? Path.Combine(root, "ffmpeg", "ffmpeg.exe") : null;

    /// <summary>The game's teams and which files belong to them. Null without a game, or if its database can't be read.</summary>
    public TeamCatalog? TeamCatalog { get; private set; }

    public FilesViewModel Files { get; }

    public ModViewModel Mod { get; }

    public SetupViewModel Setup { get; }

    public bool IsSetupPage => CurrentPage == Setup;

    public bool IsRinkPage => CurrentPage == this;

    public bool IsIcePage => CurrentPage == Ice;

    public bool IsTeamsPage => CurrentPage == Teams;

    public bool IsUniformsPage => IsTeamsPage && Teams.IsUniformsTab;

    public bool IsTeamsRailActive => IsTeamsPage && !Teams.IsUniformsTab;

    public bool IsRosterPage => CurrentPage == Roster;

    public bool IsDatabasePage => CurrentPage == Database;

    public bool IsSoundsPage => CurrentPage == Sounds;

    public bool IsVideosPage => CurrentPage == Videos;

    public bool IsTextPage => CurrentPage == Text;

    public bool IsTexturesPage => CurrentPage == Textures;

    public bool IsFilesPage => CurrentPage == Files;

    public bool IsModPage => CurrentPage == Mod;

    public bool HasGame => Index is not null;

    public bool HasProject => Project is not null;

    public bool CanInstall => HasGame && HasProject && InstallRoot is not null;

    public string GameTitle => Index is null ? "No game connected" : InstallRoot is not null ? "PS3 recomp" : "Disc image, read-only";

    public string ModTitle => Project?.Name ?? "No mod open";

    [RelayCommand]
    private void ShowSetup() => CurrentPage = Setup;

    [RelayCommand]
    private void ShowRink() => CurrentPage = this;

    [RelayCommand]
    private void ShowCenterIce()
    {
        Ice.Kind = IceKind.CenterIce;
        CurrentPage = Ice;
    }

    [RelayCommand]
    private void ShowIceLogos()
    {
        Ice.Kind = IceKind.IceLogos;
        CurrentPage = Ice;
    }

    [RelayCommand]
    private void ShowIce() => CurrentPage = Ice;

    [RelayCommand]
    private void ShowTeams()
    {
        CurrentPage = Teams;
        Teams.EnsureLoaded();
        Teams.DropForeignSelection();
        if (Teams.IsUniformsTab)
        {
            Teams.ShowSection(null);
        }

        OnPropertyChanged(nameof(IsUniformsPage));
        OnPropertyChanged(nameof(IsTeamsRailActive));
    }

    [RelayCommand]
    private void ShowUniforms()
    {
        CurrentPage = Teams;
        Teams.EnsureLoaded();
        Teams.DropForeignSelection();
        Teams.ShowSection(TeamSection.Uniforms);
        OnPropertyChanged(nameof(IsUniformsPage));
        OnPropertyChanged(nameof(IsTeamsRailActive));
    }

    [RelayCommand]
    private void ShowRoster()
    {
        CurrentPage = Roster;
        Roster.EnsureLoaded();
    }

    [RelayCommand]
    private void ShowDatabase()
    {
        CurrentPage = Database;
        Database.EnsureLoaded();
    }

    [RelayCommand]
    private void ShowSounds()
    {
        CurrentPage = Sounds;
        Sounds.EnsureLoaded();
    }

    /// <summary>Opens the Roster page on a team, from a link on another page.</summary>
    public void ShowRosterForTeam(int teamIndex)
    {
        ShowRosterCommand.Execute(null);
        Roster.ShowTeamWhenReady(teamIndex);
    }

    /// <summary>Opens the Sounds page on a team's goal horn, from a link on another page.</summary>
    public void ShowSoundsForTeam(string teamName)
    {
        ShowSoundsCommand.Execute(null);
        _ = Sounds.ShowBankWhenReadyAsync(teamName);
    }

    [RelayCommand]
    private void ShowText()
    {
        CurrentPage = Text;
        Text.EnsureLoaded();
    }

    [RelayCommand]
    private void ShowVideos()
    {
        CurrentPage = Videos;
        Videos.EnsureLoaded();
    }

    /// <summary>The Teams page's own tabs can move it on or off the Uniforms tab.</summary>
    public void TeamTabChanged()
    {
        OnPropertyChanged(nameof(IsUniformsPage));
        OnPropertyChanged(nameof(IsTeamsRailActive));
    }

    [RelayCommand]
    private void ShowTextures()
    {
        CurrentPage = Textures;
        Textures.EnsureLoaded();
    }

    [RelayCommand]
    private void ShowFiles() => CurrentPage = Files;

    [RelayCommand]
    private void ShowMod() => CurrentPage = Mod;

    /// <summary>Reconnects what was used last time, or the install if it's sitting near the app.</summary>
    public async Task StartAsync(string? gamePath = null, string? projectPath = null)
    {
        BuildRecentMods();
        projectPath ??= _settings.ProjectPath;
        if (projectPath is not null && Directory.Exists(projectPath))
        {
            OpenProject(projectPath);
        }

        gamePath ??= _settings.GamePath ?? FindInstallNearby();
        if (gamePath is not null)
        {
            await ConnectAsync(gamePath);
        }

        // Someone with no game connected is new, or has moved it. Either way, this page is where to go next.
        if (Index is null)
        {
            ShowSetup();
        }
    }

    [RelayCommand]
    private async Task PickGameAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Pick your NHL Legacy Native folder" };
        if (dialog.ShowDialog() == true)
        {
            await ConnectAsync(dialog.FolderName);
        }
    }

    [RelayCommand]
    private async Task PickDiscImageAsync()
    {
        var dialog = new OpenFileDialog { Title = "Pick your NHL Legacy disc image", Filter = "Disc images (*.iso)|*.iso" };
        if (dialog.ShowDialog() == true)
        {
            await ConnectAsync(dialog.FileName);
        }
    }

    /// <summary>Reads the game files again if the launcher's enabled mods changed. Called when the window comes back to the front.</summary>
    public async Task FollowLauncherAsync()
    {
        if (_following || BusyText is not null || _connectedPath is null || InstallRoot is not { } install
            || GameInstallLocator.EnabledModsFingerprint(install) == _connectedMods)
        {
            return;
        }

        _following = true;
        try
        {
            await ConnectAsync(_connectedPath);
            IReadOnlyList<string> enabled = GameInstallLocator.EnabledMods(install);
            Status = enabled.Count == 0
                ? "The launcher's mods changed. Showing the game with no mods on."
                : $"The launcher's mods changed. Showing: {string.Join(", ", enabled)}.";
        }
        finally
        {
            _following = false;
        }
    }

    public async Task ConnectAsync(string path)
    {
        BusyText = "Reading the game files";
        Status = $"Reading {path}";
        await Ice.StopLoadingAsync();
        await Textures.StopAsync();

        Result<GameFileIndex> opened = await Task.Run(() => GameInstallLocator.Open(path));
        BusyText = null;
        if (!opened.IsSuccess)
        {
            Status = opened.Error!;
            Dialogs.Tell("That doesn't look like the game", opened.Error!);
            return;
        }

        GameFileIndex? previous = Index;
        InstallRoot = GameInstallLocator.FindInstallRoot(path);
        Index = opened.Value;
        previous?.Dispose();

        _settings.GamePath = path;
        SaveSettings();
        _connectedPath = path;
        _connectedMods = InstallRoot is { } root ? GameInstallLocator.EnabledModsFingerprint(root) : null;

        Status = $"Connected: {Index!.Files.Count:N0} game files";
        GameFileIndex connected = Index!;
        Result<TeamCatalog> teams = await Task.Run(() => TeamCatalog.Build(connected));
        TeamCatalog = teams.IsSuccess ? teams.Value : null;
        if (!teams.IsSuccess)
        {
            Status = $"Connected, but its teams couldn't be read: {teams.Error}";
        }

        Ice.Load();
        Textures.Reset();
        Teams.Reset();
        Roster.Reset();
        Database.Reset();
        Sounds.Reset();
        Videos.Reset();
        Text.Reset();
        Files.Load();
        await RefreshProjectAsync();
    }

    [RelayCommand]
    private void NewMod()
    {
        EnsureProject(forceNew: true);
    }

    /// <summary>Makes sure there's a mod to save into, asking for a name if needed.</summary>
    public bool EnsureProject(bool forceNew = false)
    {
        if (Project is not null && !forceNew)
        {
            return true;
        }

        string? name = Dialogs.Ask("Name your mod", "This is the name it gets in the game's launcher. Your edits are saved in its own folder, and your game files stay as they are.", "Create mod", "My Mod");
        if (name is null)
        {
            return false;
        }

        string? clean = ModNames.Clean(name);
        if (clean is null)
        {
            Dialogs.Tell("Try another name", $"\"{name}\" can't be used as a mod name.");
            return false;
        }

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NHL Legacy Workbench", "Mods", clean);
        Result<ModProject> created = ModProject.Create(folder, clean);
        if (!created.IsSuccess)
        {
            Dialogs.Tell("Couldn't create that mod", created.Error!);
            return false;
        }

        SetProject(created.Value!);
        _ = RefreshProjectAsync();
        return true;
    }

    [RelayCommand]
    private async Task OpenModAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Pick a mod project folder" };
        if (dialog.ShowDialog() == true)
        {
            await OpenModFolderAsync(dialog.FolderName);
        }
    }

    public async Task OpenModFolderAsync(string folder)
    {
        if (OpenProject(folder))
        {
            await RefreshProjectAsync();
        }
    }

    /// <summary>Goes back to a mod used lately. One that can't be opened any more leaves the list.</summary>
    [RelayCommand]
    public async Task OpenRecentModAsync(RecentMod? mod)
    {
        if (mod is null)
        {
            return;
        }

        if (OpenProject(mod.Folder))
        {
            await RefreshProjectAsync();
            return;
        }

        _settings.RecentMods.RemoveAll(f => SameFolder(f, mod.Folder));
        SaveSettings();
        BuildRecentMods();
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The list on My Mod: folders that are still there, without the mod that's open.</summary>
    private void BuildRecentMods()
    {
        RecentMods = _settings.RecentMods
            .Where(f => Directory.Exists(f) && (Project is null || !SameFolder(f, Project.Folder)))
            .Take(5)
            .Select(f => new RecentMod(Path.GetFileName(f.TrimEnd('\\', '/')), f))
            .ToList();
    }

    private bool OpenProject(string folder)
    {
        Result<ModProject> opened = ModProject.Open(folder);
        if (!opened.IsSuccess)
        {
            Status = opened.Error!;
            return false;
        }

        SetProject(opened.Value!);
        return true;
    }

    private void SetProject(ModProject project)
    {
        Project = project;
        _settings.ProjectPath = project.Folder;
        _settings.RecentMods.RemoveAll(f => SameFolder(f, project.Folder));
        _settings.RecentMods.Insert(0, project.Folder);
        if (_settings.RecentMods.Count > 12)
        {
            _settings.RecentMods.RemoveRange(12, _settings.RecentMods.Count - 12);
        }

        SaveSettings();
        BuildRecentMods();
        Status = $"Working on {project.Name}";
    }

    /// <summary>Re-runs the checks and updates the scoreboard, mod page and logo marks.</summary>
    public async Task RefreshProjectAsync()
    {
        ModProject? project = Project;
        GameFileIndex? index = Index;
        IReadOnlyList<ModIssue> issues = [];
        if (project is not null)
        {
            try
            {
                issues = await Task.Run(() => ModValidator.Check(project, index));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = $"The checks couldn't finish: {ex.Message}";
            }
        }

        // An empty mod isn't a problem yet. It's just where everyone starts.
        ModIssue[] shown = issues.Where(i => i.Code != ModIssueCode.EmptyProject).ToArray();
        EditCount = (project?.Files.Count ?? 0) + (project?.HasRoster == true ? 1 : 0);
        ErrorCount = shown.Count(i => i.Severity == ModIssueSeverity.Error);
        WarningCount = shown.Count(i => i.Severity == ModIssueSeverity.Warning);
        Mod.Update(project, shown);
        Ice.RefreshChanged();
        Textures.RefreshChanged();
        Teams.RefreshChanged();
        Roster.ProjectChanged();
        Database.ProjectChanged();
        Sounds.ProjectChanged();
        Text.ProjectChanged();
    }

    /// <summary>A snapshot put the mod's files back, so everything showing them looks again.</summary>
    public async Task ProjectRestoredAsync(ModRestore restored)
    {
        Project = restored.Project;
        await FilesChangedAsync(restored.AffectedPaths);
    }

    /// <summary>Files in the mod changed behind the pages' backs, so each page looks again.</summary>
    public async Task FilesChangedAsync(IEnumerable<string> paths)
    {
        await RefreshProjectAsync();
        foreach (string path in paths)
        {
            Textures.FileChanged(path);
        }

        Roster.FileChangedElsewhere();
        Text.FileChangedElsewhere();
        Database.ProjectChanged();
        Sounds.ProjectChanged();
    }

    private bool CanExport() => Project is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Pick where to put the exported mod" };
        if (dialog.ShowDialog() == true)
        {
            await ExportAsync(p => ModExporter.ExportFolder(p, Path.Combine(dialog.FolderName, p.Name), Index));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportZipAsync()
    {
        var dialog = new SaveFileDialog { Title = "Save the mod as a zip", FileName = Project!.Name + ".zip", Filter = "Zip files (*.zip)|*.zip" };
        if (dialog.ShowDialog() == true)
        {
            await ExportAsync(p => ModExporter.ExportZip(p, dialog.FileName, Index));
        }
    }

    private async Task<ModExportResult?> ExportAsync(Func<ModProject, Result<ModExportResult>> export)
    {
        BusyText = "Packing the mod";
        ModProject project = Project!;
        Result<ModExportResult> result = await Task.Run(() => export(project));
        BusyText = null;
        if (!result.IsSuccess)
        {
            Status = "Export stopped.";
            Dialogs.Tell("Not exported", result.Error!);
            return null;
        }

        Status = $"Exported to {result.Value!.Path}";
        return result.Value;
    }

    private bool CanRunInstall() => CanInstall;

    /// <summary>Installs through the game's own launcher, so its mod list and settings stay in charge.</summary>
    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task InstallAsync()
    {
        string install = InstallRoot!;
        string exportFolder = Path.Combine(Project!.Folder, "export", Project.Name);
        if (RecompLauncher.WouldReplaceInstalledMod(install, exportFolder)
            && !Dialogs.Confirm("Replace the installed mod?", $"The game already has a mod called \"{Project.Name}\". Installing replaces that whole mod with this one. If it stayed switched off, turn it back on in the launcher's Mods tab.", "Replace it"))
        {
            return;
        }

        ModExportResult? exported = await ExportAsync(p => ModExporter.ExportFolder(p, exportFolder, Index));
        if (exported is null)
        {
            return;
        }

        BusyText = "Installing through the launcher";
        Result<ModInstallResult> imported = await Task.Run(() => RecompLauncher.Import(install, exported.Path));
        BusyText = null;
        if (!imported.IsSuccess || !imported.Value!.Succeeded)
        {
            string reason = imported.IsSuccess ? imported.Value!.Message : imported.Error!;
            Status = "Install stopped.";
            Dialogs.Tell("Not installed", reason);
            return;
        }

        await ConnectAsync(install);
        Status = $"{imported.Value.Message}. Press Play in the launcher to try it.";
    }

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private void OpenLauncher()
    {
        string launcher = Path.Combine(InstallRoot!, RecompLauncher.LauncherExeName);
        if (!File.Exists(launcher))
        {
            Status = $"{RecompLauncher.LauncherExeName} isn't in the game folder.";
            return;
        }

        Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true, WorkingDirectory = InstallRoot! })?.Dispose();
    }

    [RelayCommand]
    private void OpenProjectFolder()
    {
        if (Project is not null)
        {
            Process.Start(new ProcessStartInfo(Project.Folder) { UseShellExecute = true })?.Dispose();
        }
    }

    private void SaveSettings()
    {
        if (_rememberChoices)
        {
            _settings.Save();
        }
    }

    /// <summary>Looks beside the app and in the folders above it, where the release zip usually gets extracted.</summary>
    private static string? FindInstallNearby()
    {
        try
        {
            for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                DirectoryInfo? match = folder.EnumerateDirectories("NHL-Legacy-Native*").FirstOrDefault();
                if (match is not null && GameInstallLocator.FindInstallRoot(match.FullName) is { } root)
                {
                    return root;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Then the user picks it themselves.
        }

        return null;
    }
}
