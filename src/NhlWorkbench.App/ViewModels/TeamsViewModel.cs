using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A team in the list down the side.</summary>
public partial class TeamEntry : ObservableObject
{
    [ObservableProperty]
    private TextureItem? _crest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEdits))]
    private int _editCount;

    [ObservableProperty]
    private bool _isSelected;

    public required GameTeam Team { get; init; }

    public string Name => Team.Name;

    public string League => Team.League;

    public string Abbreviation => Team.Abbreviation;

    public bool HasEdits => EditCount > 0;

    public required Brush Color { get; init; }
}

/// <summary>One picture in a team's bundle.</summary>
public sealed record TeamTile(string Label, TextureItem Item);

/// <summary>A line in the bundle: a heading, or a row of tiles. The list virtualizes whole lines.</summary>
public abstract record TeamLine;

/// <param name="Kit">The uniform under this heading, if that's what it is.</param>
public sealed record TeamHeadingLine(string Title, string Note, TeamKit? Kit = null) : TeamLine;

public sealed record TeamTilesLine(IReadOnlyList<TeamTile> Tiles) : TeamLine;

/// <summary>A tab across the top of a team. Null section means the whole bundle.</summary>
public partial class TeamTab : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public TeamSection? Section { get; init; }

    public required string Name { get; init; }

    public required int Count { get; init; }

    public string Label => $"{Name}  {Count:N0}";
}

/// <summary>
/// The Teams and Uniforms pages: pick a team and see everything drawn for it. Tiles share their items with the
/// Textures page, so its details panel, editing and thumbnails all work the same way here.
/// </summary>
public partial class TeamsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly List<TeamEntry> _entries = [];
    private ListCollectionView? _view;
    private bool _isLoaded;
    private TeamSection? _section;

    [ObservableProperty]
    private ListCollectionView? _teams;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTeam), nameof(TeamFacts))]
    private TeamEntry? _selectedTeam;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TeamTab> _tabs = [];

    [ObservableProperty]
    private IReadOnlyList<TeamLine> _lines = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    public TeamsViewModel(MainViewModel main) => _main = main;

    public TexturesViewModel Textures => _main.Textures;

    public bool HasTeam => SelectedTeam is not null;

    public bool IsUniformsTab => _section == TeamSection.Uniforms;

    public string TeamFacts
    {
        get
        {
            if (SelectedTeam?.Team is not { } team)
            {
                return string.Empty;
            }

            var facts = new List<string> { team.League };
            if (team.ArenaName.Length > 0)
            {
                facts.Add(team.ArenaName);
            }

            facts.Add($"Art number {team.ArtId}");
            return string.Join(" · ", facts);
        }
    }

    public int Columns
    {
        get => field;
        set
        {
            if (value != field && value > 0)
            {
                field = value;
                BuildLines();
            }
        }
    } = 3;

    partial void OnQueryChanged(string value) => _view?.Refresh();

    partial void OnSelectedTeamChanged(TeamEntry? oldValue, TeamEntry? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        Textures.Selected = null;
        BuildTabs();
        BuildLines();
    }

    /// <summary>Forgets the old game. The new one is read the next time the page opens.</summary>
    public void Reset()
    {
        _isLoaded = false;
        _entries.Clear();
        _view = null;
        Teams = null;
        SelectedTeam = null;
        Summary = string.Empty;
        if (_main.IsTeamsPage)
        {
            EnsureLoaded();
        }
    }

    /// <summary>Lists the teams straight away, then fills in their art once the texture list has been read.</summary>
    public void EnsureLoaded()
    {
        if (_isLoaded || _main.TeamCatalog is not { } catalog)
        {
            return;
        }

        _isLoaded = true;
        foreach (GameTeam team in catalog.Teams)
        {
            _entries.Add(new TeamEntry { Team = team, Color = MakeBrush(team) });
        }

        _view = new ListCollectionView(_entries) { Filter = Matches };
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TeamEntry.League)));
        Teams = _view;
        SelectedTeam = _entries.FirstOrDefault();

        Textures.EnsureLoaded();
        _ = FillInArtAsync();
    }

    /// <summary>Waits for the art, for callers that want everything ready.</summary>
    public async Task WaitForArtAsync()
    {
        await Textures.WaitForCatalogAsync();
        Connect();
    }

    public void ShowSection(TeamSection? section)
    {
        _section = section;
        OnPropertyChanged(nameof(IsUniformsTab));
        _main.TeamTabChanged();
        foreach (TeamTab tab in Tabs)
        {
            tab.IsActive = tab.Section == section;
        }

        BuildLines();
    }

    /// <summary>Called on arriving at this page: a pick left over from the Textures page may not belong to this team.</summary>
    public void DropForeignSelection()
    {
        if (Textures.Selected is { } picked && SelectedTeam?.Team.AllArt.Any(a => a.Path == picked.File.Path) != true)
        {
            Textures.Selected = null;
        }
    }

    public void ShowTeam(GameTeam team) => SelectedTeam = _entries.FirstOrDefault(e => e.Team == team);

    public void RefreshChanged()
    {
        var changed = _main.Project?.Files.Select(f => f.Path).ToHashSet() ?? [];
        foreach (TeamEntry entry in _entries)
        {
            entry.EditCount = entry.Team.AllArt.Select(a => a.Path).Distinct().Count(changed.Contains);
        }
    }

    [RelayCommand]
    private void ShowTab(TeamTab? tab)
    {
        if (tab is not null)
        {
            ShowSection(tab.Section);
        }
    }

    [RelayCommand]
    private void SelectTile(TeamTile? tile) => Textures.Selected = tile?.Item;

    /// <summary>The team's players, on the Roster page.</summary>
    [RelayCommand]
    private void OpenRoster()
    {
        if (SelectedTeam?.Team is { HasDatabaseRow: true } team)
        {
            _main.ShowRosterForTeam(team.Index);
        }
    }

    /// <summary>The team's horn and chants, on the Sounds page.</summary>
    [RelayCommand]
    private void OpenSounds()
    {
        if (SelectedTeam?.Team is { } team)
        {
            _main.ShowSoundsForTeam(team.Name);
        }
    }

    [RelayCommand]
    private async Task ExportBundleAsync(string format)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to put the team's folder" };
        if (dialog.ShowDialog() == true)
        {
            await ExportBundleToAsync(dialog.FolderName, format == "png" ? TeamBundleFormat.Png : TeamBundleFormat.Dds);
        }
    }

    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder of pictures" };
        if (dialog.ShowDialog() == true)
        {
            await ImportBundleFromAsync(dialog.FolderName);
        }
    }

    [RelayCommand]
    private async Task ExportKitPngAsync(TeamKit? kit) => await ChooseFolderForKitAsync(kit, TeamBundleFormat.Png);

    [RelayCommand]
    private async Task ExportKitDdsAsync(TeamKit? kit) => await ChooseFolderForKitAsync(kit, TeamBundleFormat.Dds);

    private async Task ChooseFolderForKitAsync(TeamKit? kit, TeamBundleFormat format)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to put the uniform's folder" };
        if (kit is not null && dialog.ShowDialog() == true)
        {
            await ExportKitToAsync(kit, dialog.FolderName, format);
        }
    }

    /// <summary>Saves one uniform's pieces to a folder of their own. Returns the folder, or null if it failed.</summary>
    public Task<string?> ExportKitToAsync(TeamKit kit, string parent, TeamBundleFormat format) =>
        SelectedTeam?.Team is { } team
            ? ExportArtToAsync($"{team.Name} - {kit.Title}", team.ArtId, kit.Pieces.Select(p => p with { Group = string.Empty }), parent, format, sortBySection: false)
            : Task.FromResult<string?>(null);

    /// <summary>Saves the selected team's art to a folder. Returns the folder, or null if it failed.</summary>
    public Task<string?> ExportBundleToAsync(string parent, TeamBundleFormat format) =>
        SelectedTeam?.Team is { } team
            ? ExportArtToAsync(team.Name, team.ArtId, team.AllArt, parent, format, sortBySection: true)
            : Task.FromResult<string?>(null);

    /// <summary>Saves any set of pictures as a bundle that can be brought back in. Returns the folder, or null if it failed.</summary>
    public async Task<string?> ExportArtToAsync(string name, int artId, IEnumerable<TeamArt> art, string parent, TeamBundleFormat format, bool sortBySection)
    {
        if (_main.Index is not { } index)
        {
            return null;
        }

        await Textures.WaitForCatalogAsync();
        ModProject? project = _main.Project;
        var codec = new DdsCodec();
        _main.BusyText = $"Saving {name}";
        Result<TeamBundleExport> exported = await Task.Run(() => TeamBundle.Export(
            name,
            artId,
            art,
            p => Textures.FindItem(p)?.File,
            p => TexturesViewModel.ReadFile(index, project, p),
            codec,
            parent,
            format,
            EncodePng,
            (done, total) => _main.BusyText = $"Saving {name}: {done:N0} of {total:N0}",
            sortBySection));
        _main.BusyText = null;

        if (!exported.IsSuccess)
        {
            _main.Status = "The pictures weren't saved.";
            Dialogs.Tell("Not saved", exported.Error!);
            return null;
        }

        TeamBundleExport result = exported.Value!;
        string skipped = result.Problems.Count > 0 ? $" {result.Problems.Count} couldn't be read." : string.Empty;
        _main.Status = $"Saved {result.Pictures:N0} pictures to {result.Folder}.{skipped}";
        return result.Folder;
    }

    /// <summary>Puts a folder's pictures into the mod: a bundle's edited ones, or loose pictures going by their names.</summary>
    public async Task ImportBundleFromAsync(string folder)
    {
        if (_main.Index is not { } index || !_main.EnsureProject())
        {
            return;
        }

        if (!File.Exists(Path.Combine(folder, TeamBundle.ManifestName)))
        {
            await ImportLoosePicturesAsync(folder, index);
            return;
        }

        ModProject project = _main.Project!;
        var codec = new DdsCodec();
        _main.BusyText = "Reading the folder";
        Result<TeamBundleImport> imported = await Task.Run(() => TeamBundle.Import(
            folder,
            p => index.Find(p) is not null,
            p => TexturesViewModel.ReadFile(index, project, p),
            (p, bytes) =>
            {
                Result<ProjectFile> saved = project.SetFile(p, bytes, index);
                return saved.IsSuccess ? Result.Success(true) : Result.Failure<bool>(saved.Error!);
            },
            codec,
            (done, total) => _main.BusyText = $"Adding edits: {done:N0} of {total:N0}"));
        _main.BusyText = null;

        if (!imported.IsSuccess)
        {
            _main.Status = "Nothing was added.";
            Dialogs.Tell("Nothing was added", imported.Error!);
            return;
        }

        TeamBundleImport result = imported.Value!;
        await _main.RefreshProjectAsync();
        foreach (string path in result.ChangedPaths)
        {
            Textures.FileChanged(path);
        }

        if (result.Problems.Count > 0)
        {
            Dialogs.Tell($"{result.Problems.Count} picture{(result.Problems.Count == 1 ? " wasn't" : "s weren't")} added", string.Join("\n", result.Problems.Take(12)));
        }

        _main.Status = result.Pictures == 0 && result.Problems.Count == 0
            ? "No pictures in that folder have been edited."
            : $"Added {result.Pictures:N0} edited picture{(result.Pictures == 1 ? string.Empty : "s")} from {result.Team} to {project.Name}.";
    }

    private async Task ImportLoosePicturesAsync(string folder, GameFileIndex index)
    {
        await Textures.WaitForCatalogAsync();
        ModProject project = _main.Project!;
        var codec = new DdsCodec();
        List<TextureFile> known = Textures.AllFiles();
        _main.BusyText = "Reading the folder";
        var outcome = await Task.Run(() =>
        {
            Result<PictureFolderMatch> match = PictureFolder.Match(folder, known);
            if (!match.IsSuccess)
            {
                return (Error: match.Error, Pictures: 0, Changed: (IReadOnlyList<string>)[], Problems: (IReadOnlyList<string>)[]);
            }

            (int pictures, IReadOnlyList<string> changed, IReadOnlyList<string> problems) = PictureFolder.Apply(
                match.Value!.Edits,
                p => TexturesViewModel.ReadFile(index, project, p),
                (p, bytes) =>
                {
                    Result<ProjectFile> saved = project.SetFile(p, bytes, index);
                    return saved.IsSuccess ? Result.Success(true) : Result.Failure<bool>(saved.Error!);
                },
                codec,
                (done, total) => _main.BusyText = $"Adding pictures: {done:N0} of {total:N0}");
            return (Error: (string?)null, Pictures: pictures, Changed: changed, Problems: (IReadOnlyList<string>)[.. match.Value.Problems, .. problems]);
        });
        _main.BusyText = null;

        if (outcome.Error is { } error)
        {
            _main.Status = "Nothing was added.";
            Dialogs.Tell("Nothing was added", error);
            return;
        }

        await _main.RefreshProjectAsync();
        foreach (string path in outcome.Changed)
        {
            Textures.FileChanged(path);
        }

        if (outcome.Problems.Count > 0 && !_main.IsWalking)
        {
            string more = outcome.Problems.Count > 12 ? $"\n…and {outcome.Problems.Count - 12:N0} more." : string.Empty;
            Dialogs.Tell($"{outcome.Problems.Count:N0} picture{(outcome.Problems.Count == 1 ? " wasn't" : "s weren't")} added", string.Join("\n", outcome.Problems.Take(12)) + more);
        }

        _main.Status = $"Added {outcome.Pictures:N0} picture{(outcome.Pictures == 1 ? string.Empty : "s")} to {project.Name}, going by their file names. {outcome.Problems.Count:N0} left out.";
    }

    private static byte[] EncodePng(Rgba32Image image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Imaging.ToBitmap(image, int.MaxValue)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private async Task FillInArtAsync()
    {
        await Textures.WaitForCatalogAsync();
        Connect();
    }

    private void Connect()
    {
        foreach (TeamEntry entry in _entries.Where(e => e.Crest is null && e.Team.CrestPath is not null))
        {
            entry.Crest = Textures.FindItem(entry.Team.CrestPath!);
        }

        RefreshChanged();
        BuildTabs();
        BuildLines();
    }

    private bool Matches(object item)
    {
        var entry = (TeamEntry)item;
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.League.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.Abbreviation.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildTabs()
    {
        if (SelectedTeam?.Team is not { } team)
        {
            Tabs = [];
            return;
        }

        int Count(TeamSection section) => team.AllArt.Count(a => a.Section == section && Textures.FindItem(a.Path) is not null);

        var tabs = new List<TeamTab>
        {
            new() { Name = "Everything", Count = team.AllArt.Count(a => Textures.FindItem(a.Path) is not null) },
            new() { Name = "Uniforms", Section = TeamSection.Uniforms, Count = Count(TeamSection.Uniforms) },
            new() { Name = "Logos", Section = TeamSection.Logos, Count = Count(TeamSection.Logos) },
            new() { Name = "Ice and puck", Section = TeamSection.IceAndPuck, Count = Count(TeamSection.IceAndPuck) },
            new() { Name = "Arena", Section = TeamSection.Arena, Count = Count(TeamSection.Arena) },
            new() { Name = "Goalie masks", Section = TeamSection.GoalieMasks, Count = Count(TeamSection.GoalieMasks) },
        };
        foreach (TeamTab tab in tabs)
        {
            tab.IsActive = tab.Section == _section;
        }

        Tabs = tabs;
    }

    private void BuildLines()
    {
        var lines = new List<TeamLine>();
        if (SelectedTeam?.Team is { } team)
        {
            if (_section != TeamSection.Uniforms)
            {
                foreach (IGrouping<string, TeamArt> group in team.Art
                    .Where(a => _section is null || a.Section == _section)
                    .OrderBy(a => a.Section)
                    .GroupBy(a => a.Group))
                {
                    AddTiles(lines, group.Key, string.Empty, group);
                }
            }

            if (_section is null or TeamSection.Uniforms)
            {
                foreach (TeamKit kit in team.Kits)
                {
                    AddTiles(lines, kit.Title, kit.Note, kit.Pieces, kit);
                }
            }
        }

        Lines = lines;
        Summary = !Textures.HasCatalog
            ? "Reading the game's textures…"
            : lines.Count == 0 ? "Nothing here for this team." : string.Empty;
    }

    private void AddTiles(List<TeamLine> lines, string title, string note, IEnumerable<TeamArt> art, TeamKit? kit = null)
    {
        List<TeamTile> tiles = art
            .Select(a => (a, Item: Textures.FindItem(a.Path)))
            .Where(p => p.Item is not null)
            .Select(p => new TeamTile(p.a.Label, p.Item!))
            .ToList();
        if (tiles.Count == 0)
        {
            return;
        }

        lines.Add(new TeamHeadingLine(title, note, kit));
        lines.AddRange(tiles.Chunk(Columns).Select(chunk => new TeamTilesLine(chunk)));
    }

    private static Brush MakeBrush(GameTeam team)
    {
        var brush = new SolidColorBrush(team.HasDatabaseRow ? System.Windows.Media.Color.FromRgb(team.Primary.R, team.Primary.G, team.Primary.B) : Colors.Transparent);
        brush.Freeze();
        return brush;
    }
}
