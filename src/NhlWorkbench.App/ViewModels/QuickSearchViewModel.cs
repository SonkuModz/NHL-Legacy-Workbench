using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Roster;

namespace NhlWorkbench.App.ViewModels;

/// <summary>Something quick search found, and how to go to it.</summary>
public sealed record QuickResult(string Kind, string Title, string Detail, Action Open);

/// <summary>
/// Ctrl+K: type anything and jump to it, whether that's a team, a player, a sound, a picture, a string or a page. It only
/// looks through what's already been read, and starts the reading when it opens, so results fill in as pages get ready.
/// </summary>
public partial class QuickSearchViewModel : ObservableObject
{
    private const int MostOfEach = 6;

    private readonly MainViewModel _main;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<QuickResult> _results = [];

    [ObservableProperty]
    private QuickResult? _selected;

    [ObservableProperty]
    private string _hint = string.Empty;

    public QuickSearchViewModel(MainViewModel main) => _main = main;

    partial void OnQueryChanged(string value) => Search();

    [RelayCommand]
    public void Open()
    {
        IsOpen = true;
        Query = string.Empty;
        if (_main.Index is not null)
        {
            _main.Textures.EnsureLoaded();
            _main.Roster.EnsureLoaded();
            _main.Sounds.EnsureLoaded();
            _main.Text.EnsureLoaded();
        }

        Search();
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Goes to a result, or the one picked in the list.</summary>
    [RelayCommand]
    public void Go(QuickResult? result)
    {
        if ((result ?? Selected) is { } chosen)
        {
            IsOpen = false;
            chosen.Open();
        }
    }

    /// <summary>Moves the pick up or down the list, for the arrow keys.</summary>
    public void Move(int by)
    {
        if (Results.Count > 0)
        {
            int at = Selected is null ? -1 : Results.ToList().IndexOf(Selected);
            Selected = Results[Math.Clamp(at + by, 0, Results.Count - 1)];
        }
    }

    public void Search()
    {
        string text = Query.Trim();
        var found = new List<QuickResult>();
        AddPages(found, text);
        if (text.Length >= 2 && _main.Index is not null)
        {
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            AddTeams(found, words);
            AddPlayers(found, text);
            AddSounds(found, words);
            AddPictures(found, words);
            AddText(found, words);
            found.Add(new QuickResult("Files", $"Look for \"{text}\" in the game's files", "Explorer", () =>
            {
                _main.ShowFilesCommand.Execute(null);
                _main.Files.Query = text;
            }));
        }

        Results = found;
        Selected = found.FirstOrDefault();
        Hint = _main.Index is null
            ? "Connect your game on the Rink page to search it."
            : "Teams, players, sounds, pictures, text and pages. Enter opens, Esc closes.";
    }

    private void AddPages(List<QuickResult> found, string text)
    {
        var pages = new (string Name, string Detail, Action Open)[]
        {
            ("Setup", "Getting started", () => _main.ShowSetupCommand.Execute(null)),
            ("Rink", "Home", () => _main.ShowRinkCommand.Execute(null)),
            ("Teams", "Each team's art", () => _main.ShowTeamsCommand.Execute(null)),
            ("Uniforms", "Every kit", () => _main.ShowUniformsCommand.Execute(null)),
            ("Roster", "Players, lines and ratings", () => _main.ShowRosterCommand.Execute(null)),
            ("Ice", "Center ice and rink logos", () => _main.ShowIceCommand.Execute(null)),
            ("Textures", "Every picture in the game", () => _main.ShowTexturesCommand.Execute(null)),
            ("Sounds", "Horns, chants and crowds", () => _main.ShowSoundsCommand.Execute(null)),
            ("Videos", "Every movie", () => _main.ShowVideosCommand.Execute(null)),
            ("Text", "Team names and menus", () => _main.ShowTextCommand.Execute(null)),
            ("Explorer", "Every game file", () => _main.ShowFilesCommand.Execute(null)),
            ("Database", "The game's tables", () => _main.ShowDatabaseCommand.Execute(null)),
            ("My Mod", "Your changes, snapshots and installing", () => _main.ShowModCommand.Execute(null)),
        };
        found.AddRange(pages
            .Where(p => text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(p => new QuickResult("Page", p.Name, p.Detail, p.Open)));
    }

    private void AddTeams(List<QuickResult> found, string[] words)
    {
        foreach (GameTeam team in (_main.TeamCatalog?.Teams ?? [])
            .Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Abbreviation.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .Take(MostOfEach))
        {
            found.Add(new QuickResult("Team", team.Name, team.League, () =>
            {
                _main.ShowTeamsCommand.Execute(null);
                _main.Teams.ShowTeam(team);
            }));
        }
    }

    private void AddPlayers(List<QuickResult> found, string text)
    {
        foreach ((RosterPlayer player, string team) in _main.Roster.FindPlayers(text, MostOfEach))
        {
            found.Add(new QuickResult("Player", player.FullName, $"{player.PositionName} {player.Overall} · {team}", () =>
            {
                _main.ShowRosterCommand.Execute(null);
                _main.Roster.ShowPlayer(player.Team, player.PlayerId);
            }));
        }
    }

    private void AddSounds(List<QuickResult> found, string[] words)
    {
        foreach (SoundBankEntry bank in _main.Sounds.FindBanks(words).Take(MostOfEach))
        {
            // Say the team that was searched for, not whichever team happens to be first on a shared bank.
            string team = bank.Info.Teams.FirstOrDefault(t => words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase))) ?? bank.Title;
            int others = bank.Info.Teams.Count - 1;
            string shared = others > 0 ? $" · shared with {others} other team{(others == 1 ? string.Empty : "s")}" : string.Empty;
            found.Add(new QuickResult("Sound", team, bank.Info.Group + shared, () =>
            {
                _main.ShowSoundsCommand.Execute(null);
                _main.Sounds.ShowEntry(bank);
            }));
        }
    }

    private void AddPictures(List<QuickResult> found, string[] words)
    {
        foreach (TextureItem item in _main.Textures.FindItems(words).Take(MostOfEach))
        {
            found.Add(new QuickResult("Picture", item.Owner ?? item.Title, item.File.Path, () =>
            {
                _main.ShowTexturesCommand.Execute(null);
                _main.Textures.Query = item.Title;
                _main.Textures.Selected = item;
            }));
        }
    }

    private void AddText(List<QuickResult> found, string[] words)
    {
        foreach (TextRow row in _main.Text.FindRows(words).Take(MostOfEach))
        {
            // Some strings carry line breaks, which would throw the list's rows out.
            string oneLine = string.Join(' ', row.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            found.Add(new QuickResult("Text", oneLine, row.Key, () =>
            {
                _main.ShowTextCommand.Execute(null);
                _main.Text.ShowRow(row);
            }));
        }
    }
}
