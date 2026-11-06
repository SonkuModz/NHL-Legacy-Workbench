using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Roster;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A team in the list down the side, or the free agents.</summary>
public partial class RosterTeamEntry : ObservableObject
{
    [ObservableProperty]
    private TextureItem? _crest;

    public required int Index { get; init; }

    public required string Name { get; init; }

    public required string League { get; init; }

    public string Abbreviation { get; init; } = string.Empty;

    public GameTeam? Team { get; init; }

    public required Brush Color { get; init; }
}

/// <summary>A player in the middle list.</summary>
public partial class RosterRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Letter), nameof(HasLetter))]
    private RosterPlayer _player;

    public RosterRow(RosterPlayer player, string group, string note)
    {
        _player = player;
        Group = group;
        Note = note;
    }

    public string Group { get; }

    /// <summary>The line they play on, or their team in search results.</summary>
    public string Note { get; }

    public string Letter => Player.Captain switch { RosterFile.Captain => "C", RosterFile.Alternate => "A", _ => string.Empty };

    public bool HasLetter => Letter.Length > 0;
}

/// <summary>One of a player's ratings. The value is committed as soon as the box loses focus.</summary>
public partial class RatingRow : ObservableObject
{
    private readonly RosterViewModel _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarWidth))]
    private int _value;

    public RatingRow(RosterViewModel owner, RosterRating rating, int value)
    {
        _owner = owner;
        Rating = rating;
        _value = value;
    }

    public RosterRating Rating { get; }

    /// <summary>The filled part of the 74-pixel bar beside the box.</summary>
    public double BarWidth => 74 * Math.Clamp((Value - RosterFile.LowestRating) / (double)(RosterFile.HighestRating - RosterFile.LowestRating), 0, 1);

    public string Text
    {
        get => Value.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (value != Text)
            {
                _owner.CommitRating(this, value);
            }

            OnPropertyChanged();
        }
    }

    public void Show(int value)
    {
        Value = value;
        OnPropertyChanged(nameof(Text));
    }
}

public sealed record RatingGroup(string Title, IReadOnlyList<RatingRow> Ratings);

/// <summary>A button in a row of choices, such as Left and Right.</summary>
public partial class RosterChoiceEntry : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public required string Label { get; init; }

    public required int Value { get; init; }

    public required Func<RosterFile, RosterPlayer, int, Result<bool>> Apply { get; init; }
}

/// <summary>A roster that can be looked at, shown as a button when none is in the mod yet.</summary>
public partial class RosterSourceEntry : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public required RosterSource Source { get; init; }

    public string Label => Source.Title;
}

/// <summary>
/// The Roster page. It shows the roster in the current mod, or lets the user look through the others and copy one into
/// the mod to edit. Only the mod's own copy is ever written: installed mods and the game's saves are only read.
/// </summary>
public partial class RosterViewModel : ObservableObject
{
    private static readonly Brush NoColor = MakeFrozen(Colors.Transparent);

    private readonly MainViewModel _main;
    private readonly List<RosterTeamEntry> _entries = [];
    private ListCollectionView? _teamView;
    private RosterFile? _roster;
    private string? _loadedKey;
    private bool _isLoaded;
    private bool _showing;
    private bool _saving;
    private bool _saveAgain;
    private Task _saveTask = Task.CompletedTask;
    private string _savedNote = string.Empty;

    [ObservableProperty]
    private ListCollectionView? _teams;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeamFacts), nameof(HasTeam))]
    private RosterTeamEntry? _selectedTeam;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(NoteHeading))]
    private string _playerQuery = string.Empty;

    [ObservableProperty]
    private ListCollectionView? _rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer), nameof(IsSkater), nameof(HandHeading), nameof(CanLetter), nameof(PlayerFacts), nameof(TradeTargetText), nameof(CanConfirmTrade))]
    [NotifyCanExecuteChangedFor(nameof(StartTradeCommand), nameof(ConfirmTradeCommand))]
    private RosterRow? _selectedRow;

    [ObservableProperty]
    private IReadOnlyList<RosterSourceEntry> _sources = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(CanCopy), nameof(ReadOnlyNote))]
    [NotifyCanExecuteChangedFor(nameof(StartTradeCommand), nameof(ConfirmTradeCommand), nameof(CopyIntoModCommand))]
    private RosterSource? _source;

    [ObservableProperty]
    private string _sourceTitle = string.Empty;

    [ObservableProperty]
    private string _sourceNote = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrading), nameof(TradeHeading), nameof(TradeTargetText), nameof(CanConfirmTrade))]
    [NotifyCanExecuteChangedFor(nameof(StartTradeCommand), nameof(ConfirmTradeCommand))]
    private RosterPlayer? _tradeFrom;

    [ObservableProperty]
    private IReadOnlyList<RatingGroup> _ratingGroups = [];

    [ObservableProperty]
    private IReadOnlyList<RosterChoiceEntry> _positions = [];

    [ObservableProperty]
    private IReadOnlyList<RosterChoiceEntry> _hands = [];

    [ObservableProperty]
    private IReadOnlyList<RosterChoiceEntry> _letters = [];

    [ObservableProperty]
    private IReadOnlyList<RosterChoiceEntry> _styles = [];

    [ObservableProperty]
    private string _firstName = string.Empty;

    [ObservableProperty]
    private string _lastName = string.Empty;

    [ObservableProperty]
    private string _birthplace = string.Empty;

    [ObservableProperty]
    private string _jersey = string.Empty;

    [ObservableProperty]
    private string _height = string.Empty;

    [ObservableProperty]
    private string _weight = string.Empty;

    [ObservableProperty]
    private string _birthDate = string.Empty;

    public RosterViewModel(MainViewModel main) => _main = main;

    public TexturesViewModel Textures => _main.Textures;

    public bool HasTeam => SelectedTeam is not null;

    public bool HasPlayer => SelectedRow is not null;

    public bool IsSkater => SelectedRow?.Player.IsGoalie == false;

    public bool CanLetter => SelectedRow?.Player.IsFreeAgent == false;

    public bool IsSearching => PlayerQuery.Trim().Length >= 2;

    public string NoteHeading => IsSearching ? "TEAM" : "LINE";

    public string HandHeading => IsSkater ? "SHOOTS" : "CATCHES";

    /// <summary>True when the roster on show is the mod's own, which is the only one that can change.</summary>
    public bool IsEditing => Source is null && _roster?.IsEditable == true;

    public bool CanCopy => Source is { IsBuiltIn: false };

    public string ReadOnlyNote => IsEditing ? string.Empty : "Read-only. Copy this roster into your mod to change it.";

    public bool IsTrading => TradeFrom is not null;

    public string TradeHeading => TradeFrom is null ? string.Empty : $"Swapping {TradeFrom.FullName}";

    public string TradeTargetText =>
        TradeFrom is null ? string.Empty
        : SelectedRow is { } row && row.Player.PlayerId != TradeFrom.PlayerId ? $"for {row.Player.FullName}?"
        : "Pick who they swap with, on another team or in the free agents.";

    public bool CanConfirmTrade => TradeFrom is not null && SelectedRow is { } row && row.Player.PlayerId != TradeFrom.PlayerId;

    public string TeamFacts
    {
        get
        {
            if (SelectedTeam is not { } team || _roster is null)
            {
                return string.Empty;
            }

            int players = _roster.PlayersOn(team.Index).Count;
            int open = team.Index == RosterFile.FreeAgents ? 0 : _roster.OpenSpotsOn(team.Index);
            string count = $"{players} player{(players == 1 ? string.Empty : "s")}";
            return open > 0 ? $"{team.League} · {count} · {open} open spot{(open == 1 ? string.Empty : "s")}" : $"{team.League} · {count}";
        }
    }

    public string PlayerFacts
    {
        get
        {
            if (SelectedRow?.Player is not { } player)
            {
                return string.Empty;
            }

            string style = player.StyleName;
            return style.Length > 0 ? $"{player.PositionName} · {style} · {player.Line}" : $"{player.PositionName} · {player.Line}";
        }
    }

    partial void OnQueryChanged(string value) => _teamView?.Refresh();

    partial void OnPlayerQueryChanged(string value) => BuildRows();

    partial void OnSelectedTeamChanged(RosterTeamEntry? value)
    {
        if (value is not null && PlayerQuery.Length > 0)
        {
            PlayerQuery = string.Empty;
            return;
        }

        BuildRows();
    }

    partial void OnSelectedRowChanged(RosterRow? value) => ShowPlayer(value?.Player);

    partial void OnFirstNameChanged(string value) => Commit((r, p) => r.SetName(p, value, LastName));

    partial void OnLastNameChanged(string value) => Commit((r, p) => r.SetName(p, FirstName, value));

    partial void OnBirthplaceChanged(string value) => Commit((r, p) => r.SetBirthplace(p, value));

    partial void OnJerseyChanged(string value) =>
        Commit((r, p) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? r.SetJersey(p, n) : Result.Failure<bool>("A number has to be from 0 to 99."));

    partial void OnHeightChanged(string value) =>
        Commit((r, p) => ParseHeight(value) is { } inches ? r.SetHeight(p, inches) : Result.Failure<bool>("Write a height like 6'1\"."));

    partial void OnWeightChanged(string value) =>
        Commit((r, p) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? r.SetWeight(p, n) : Result.Failure<bool>("Write a weight in pounds."));

    partial void OnBirthDateChanged(string value) =>
        Commit((r, p) => DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? r.SetBirthDate(p, date.Year, date.Month, date.Day)
            : Result.Failure<bool>("Write a date like 1987-08-07."));

    /// <summary>Forgets the old game. The new one is read the next time the page opens.</summary>
    public void Reset()
    {
        _isLoaded = false;
        _loadedKey = null;
        _roster = null;
        Source = null;
        _entries.Clear();
        _teamView = null;
        Teams = null;
        SelectedTeam = null;
        Rows = null;
        SelectedRow = null;
        TradeFrom = null;
        Sources = [];
        Message = string.Empty;
        ResetTools(isMine: false);
        LineUnitRows = [];
        if (_main.IsRosterPage)
        {
            EnsureLoaded();
        }
    }

    /// <summary>Lists the teams and opens the mod's roster, or the one the game is using.</summary>
    public void EnsureLoaded()
    {
        if (_isLoaded || _main.Index is null)
        {
            return;
        }

        _isLoaded = true;
        Textures.EnsureLoaded();
        _ = OpenBestAsync();
    }

    /// <summary>Called when the mod may have changed. A roster added, removed or swapped by another page gets reread.</summary>
    public void ProjectChanged()
    {
        if (_isLoaded && !_saving && KeyForCurrentMod() is { } key && key != _loadedKey && (Source is null || _main.Project?.HasRoster == true))
        {
            _ = OpenBestAsync();
        }
        else if (_isLoaded && !_saving && KeyForCurrentMod() is null && Source is null)
        {
            _ = OpenBestAsync();
        }
    }

    /// <summary>The Database page changed the mod's roster, so what's on show here is out of date.</summary>
    public void FileChangedElsewhere()
    {
        if (_isLoaded)
        {
            _loadedKey = null;
            ProjectChanged();
        }
    }

    /// <summary>Waits for the page to finish opening a roster and saving it, for callers that need that done.</summary>
    public async Task WaitUntilReadyAsync()
    {
        while (_opening is { IsCompleted: false } || _saving)
        {
            await Task.Delay(50);
        }

        await _saveTask;
    }

    /// <summary>Called by the page's rows through a command, and by the checks in --shots mode.</summary>
    public void ShowTeam(int index) => SelectedTeam = _entries.FirstOrDefault(e => e.Index == index);

    /// <summary>Shows a team as soon as the page has a roster open, which may be right now.</summary>
    public void ShowTeamWhenReady(int index)
    {
        if (_opening is { IsCompleted: false } opening)
        {
            opening.ContinueWith(_ => ShowTeam(index), TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            ShowTeam(index);
        }

        PlayerQuery = string.Empty;
    }

    /// <summary>Players anywhere on the roster on show, with their teams' names, for quick search.</summary>
    public IEnumerable<(RosterPlayer Player, string Team)> FindPlayers(string text, int limit)
    {
        if (_roster is not { } roster)
        {
            return [];
        }

        var names = _entries.ToDictionary(e => e.Index, e => e.Name);
        return roster.Search(text, limit * 4).Where(p => names.ContainsKey(p.Team)).DistinctBy(p => p.PlayerId).Take(limit).Select(p => (p, names[p.Team]));
    }

    /// <summary>Opens a team with one of its players picked.</summary>
    public void ShowPlayer(int team, long playerId)
    {
        Tab = PlayersTab;
        PlayerQuery = string.Empty;
        ShowTeam(team);
        SelectedRow = Rows?.Cast<RosterRow>().FirstOrDefault(r => r.Player.PlayerId == playerId);
    }

    public void SelectPlayer(string lastName) =>
        SelectedRow = Rows?.Cast<RosterRow>().FirstOrDefault(r => r.Player.LastName == lastName);

    internal void CommitRating(RatingRow row, string text)
    {
        Commit((r, p) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? r.SetRating(p, row.Rating.Field, value)
            : Result.Failure<bool>($"A rating has to be from {RosterFile.LowestRating} to {RosterFile.HighestRating}."));
    }

    private Task? _opening;

    private Task OpenBestAsync() => _opening = OpenBestCoreAsync();

    private async Task OpenBestCoreAsync()
    {
        GameFileIndex? index = _main.Index;
        string? install = _main.InstallRoot;
        IReadOnlyList<RosterSource> found = await Task.Run(() => RosterSources.Find(install, index));
        Sources = found.Select(s => new RosterSourceEntry { Source = s }).ToList();

        if (_main.Project is { HasRoster: true } project)
        {
            await OpenAsync(new RosterSource(project.Name, string.Empty, project.RosterPath, false), isMine: true);
        }
        else if (Sources.FirstOrDefault() is { } first)
        {
            await OpenAsync(first.Source, isMine: false);
        }
        else
        {
            Message = "No roster was found in this game.";
        }
    }

    private async Task OpenAsync(RosterSource source, bool isMine)
    {
        Message = "Reading the roster…";
        GameFileIndex? index = _main.Index;
        Result<RosterFile> opened = await Task.Run(() => RosterSources.Open(source, index));
        if (!opened.IsSuccess)
        {
            _roster = null;
            Rows = null;
            Message = opened.Error!;
            _main.Status = $"That roster couldn't be read: {opened.Error}";
            return;
        }

        int? keepTeam = SelectedTeam?.Index;
        _roster = opened.Value;
        _loadedKey = isMine ? KeyForCurrentMod() : null;
        Message = string.Empty;
        Source = isMine ? null : source;
        ResetTools(isMine);
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ReadOnlyNote));
        foreach (RosterSourceEntry entry in Sources)
        {
            entry.IsActive = entry.Source == source;
        }

        SourceTitle = isMine ? $"Editing the roster in {source.Title}" : source.Title;
        SourceNote = isMine
            ? "Changes save to your mod straight away. Choose this mod's roster in the launcher to play it."
            : source.Note;
        await BuildTeamsAsync(keepTeam);
    }

    private async Task BuildTeamsAsync(int? keepTeam)
    {
        RosterFile roster = _roster!;
        HashSet<int> onRoster = await Task.Run(roster.TeamsOnRoster);
        _entries.Clear();
        if (roster.PlayersOn(RosterFile.FreeAgents).Count > 0)
        {
            _entries.Add(new RosterTeamEntry { Index = RosterFile.FreeAgents, Name = "Free agents", League = "Unsigned", Color = NoColor });
        }

        foreach (GameTeam team in _main.TeamCatalog?.Teams ?? [])
        {
            if (team.HasDatabaseRow && onRoster.Contains(team.Index))
            {
                _entries.Add(new RosterTeamEntry
                {
                    Index = team.Index,
                    Name = team.Name,
                    League = team.League,
                    Abbreviation = team.Abbreviation,
                    Team = team,
                    Color = MakeFrozen(System.Windows.Media.Color.FromRgb(team.Primary.R, team.Primary.G, team.Primary.B)),
                });
            }
        }

        _teamView = new ListCollectionView(_entries) { Filter = Matches };
        _teamView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RosterTeamEntry.League)));
        Teams = _teamView;
        SelectedTeam = _entries.FirstOrDefault(e => e.Index == keepTeam) ?? _entries.FirstOrDefault(e => e.Team is not null) ?? _entries.FirstOrDefault();
        BuildRows();

        await Textures.WaitForCatalogAsync();
        foreach (RosterTeamEntry entry in _entries.Where(e => e.Crest is null && e.Team?.CrestPath is not null))
        {
            entry.Crest = Textures.FindItem(entry.Team!.CrestPath!);
        }
    }

    private void BuildRows()
    {
        var keep = SelectedRow?.Player;
        RosterFile? roster = _roster;
        List<RosterRow> rows = [];
        if (roster is not null)
        {
            if (IsSearching)
            {
                var names = _entries.ToDictionary(e => e.Index, e => e.Name);
                rows = roster.Search(PlayerQuery.Trim(), 200)
                    .Where(p => names.ContainsKey(p.Team))
                    .Select(p => new RosterRow(p, "Search results", names[p.Team]))
                    .ToList();
            }
            else if (SelectedTeam is { } team)
            {
                rows = roster.PlayersOn(team.Index).OrderBy(p => GroupOrder(p)).Select(p => new RosterRow(p, GroupFor(p), p.Line)).ToList();
            }
        }

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RosterRow.Group)));
        Rows = view;
        OnPropertyChanged(nameof(TeamFacts));
        if (IsLinesTab)
        {
            BuildLines();
        }

        SelectedRow = keep is null ? null : rows.FirstOrDefault(r => r.Player.SlotRow == keep.SlotRow && r.Player.FreeAgentRow == keep.FreeAgentRow);
    }

    private static int GroupOrder(RosterPlayer player) => player.Position == RosterFile.GoaliePosition ? 2 : player.Position == 3 ? 1 : 0;

    private static string GroupFor(RosterPlayer player) => player.Position switch
    {
        RosterFile.GoaliePosition => "Goalies",
        3 => "Defense",
        _ => "Forwards",
    };

    private bool Matches(object item)
    {
        var entry = (RosterTeamEntry)item;
        return Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.League.Contains(w, StringComparison.OrdinalIgnoreCase)
            || entry.Abbreviation.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowPlayer(RosterPlayer? player)
    {
        _showing = true;
        Problem = null;
        if (player is null)
        {
            RatingGroups = [];
            _showing = false;
            return;
        }

        FillBoxes(player);

        Positions = Choices(RosterFile.PositionNames.Take(RosterFile.GoaliePosition).Select((name, i) => new RosterChoice(i, name)), player.Position, (r, p, v) => r.SetPosition(p, v));
        Hands = Choices([new(0, "Left"), new(1, "Right")], player.Handedness, (r, p, v) => r.SetHandedness(p, v));
        Letters = Choices([new(RosterFile.NoCaptain, "None"), new(RosterFile.Captain, "Captain"), new(RosterFile.Alternate, "Alternate")], player.Captain, (r, p, v) => r.SetCaptain(p, v));
        Styles = Choices(player.IsGoalie ? RosterFile.GoalieStyles : RosterFile.SkaterStyles, player.Style, (r, p, v) => r.SetStyle(p, v));

        RosterFile roster = _roster!;
        RatingGroups = (player.IsGoalie ? RosterFile.GoalieRatings : RosterFile.SkaterRatings)
            .GroupBy(r => r.Group)
            .Select(g => new RatingGroup(g.Key, g.Select(r => new RatingRow(this, r, roster.RatingOf(player, r.Field))).ToList()))
            .ToList();
        _showing = false;
    }

    private static List<RosterChoiceEntry> Choices(IEnumerable<RosterChoice> choices, int current, Func<RosterFile, RosterPlayer, int, Result<bool>> apply) =>
        choices.Select(c => new RosterChoiceEntry { Label = c.Label, Value = c.Value, IsActive = c.Value == current, Apply = apply }).ToList();

    /// <summary>Puts what's stored back in the boxes without rebuilding them, so tabbing between them keeps the cursor.</summary>
    private void RefreshPlayer(RosterPlayer player)
    {
        _showing = true;
        FillBoxes(player);
        Mark(Positions, player.Position);
        Mark(Hands, player.Handedness);
        Mark(Letters, player.Captain);
        Mark(Styles, player.Style);
        foreach (RatingRow rating in RatingGroups.SelectMany(g => g.Ratings))
        {
            rating.Show(_roster!.RatingOf(player, rating.Rating.Field));
        }

        _showing = false;
    }

    private static void Mark(IReadOnlyList<RosterChoiceEntry> choices, int current)
    {
        foreach (RosterChoiceEntry choice in choices)
        {
            choice.IsActive = choice.Value == current;
        }
    }

    private void FillBoxes(RosterPlayer player)
    {
        FirstName = player.FirstName;
        LastName = player.LastName;
        Birthplace = player.Birthplace;
        Jersey = player.Jersey.ToString(CultureInfo.InvariantCulture);
        Height = $"{player.HeightInches / 12}'{player.HeightInches % 12}\"";
        Weight = player.WeightPounds.ToString(CultureInfo.InvariantCulture);
        BirthDate = $"{player.BirthYear:0000}-{player.BirthMonth:00}-{player.BirthDay:00}";
    }

    /// <summary>Applies one change to the player on show, then rereads them, so every box shows what's really stored.</summary>
    private void Commit(Func<RosterFile, RosterPlayer, Result<bool>> change)
    {
        if (_showing || _roster is not { } roster || SelectedRow is not { } row)
        {
            return;
        }

        Result<bool> done = IsEditing ? change(roster, row.Player) : Result.Failure<bool>("This roster is read-only.");
        string? problem = done.IsSuccess ? null : done.Error;

        // Put the boxes right after this binding has finished updating, or WPF keeps showing what was typed.
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (SelectedRow == row && _roster == roster)
            {
                // Other rows can change too: a new captain takes the letter from the old one.
                foreach (RosterRow other in Rows?.Cast<RosterRow>() ?? [])
                {
                    other.Player = roster.Reread(other.Player) ?? other.Player;
                }

                if (GroupFor(row.Player) != row.Group && !IsSearching)
                {
                    BuildRows();
                }
                else
                {
                    RefreshPlayer(row.Player);
                }

                Problem = problem;
                OnPropertyChanged(nameof(PlayerFacts));
            }
        });

        if (done.IsSuccess)
        {
            Changed($"the change to {row.Player.FullName}");
        }
    }

    private bool CanStartTrade() => IsEditing && !IsTrading && SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanStartTrade))]
    private void StartTrade() => TradeFrom = SelectedRow?.Player;

    [RelayCommand]
    private void CancelTrade() => TradeFrom = null;

    private bool CanFinishTrade() => IsEditing && CanConfirmTrade;

    [RelayCommand(CanExecute = nameof(CanFinishTrade))]
    private void ConfirmTrade()
    {
        if (_roster is not { } roster || TradeFrom is not { } from || SelectedRow is not { } row)
        {
            return;
        }

        Result<string> traded = roster.Trade(from, row.Player);
        if (!traded.IsSuccess)
        {
            Problem = traded.Error;
            _main.Status = traded.Error!;
            return;
        }

        TradeFrom = null;
        _savedNote = traded.Value!;
        _main.Status = traded.Value!;
        Problem = null;
        Changed($"the swap of {from.FullName} and {row.Player.FullName}");
        BuildRows();
    }

    [RelayCommand]
    private void Choose(RosterChoiceEntry? choice)
    {
        if (choice is not null)
        {
            Commit((r, p) => choice.Apply(r, p, choice.Value));
        }
    }

    [RelayCommand]
    private void ShowSource(RosterSourceEntry? entry)
    {
        if (entry is not null && entry.Source != Source)
        {
            _ = _opening = OpenAsync(entry.Source, isMine: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyIntoModAsync()
    {
        if (Source is not { SavePath: { } path } source || !_main.EnsureProject())
        {
            return;
        }

        ModProject project = _main.Project!;
        if (project.HasRoster && !Dialogs.Confirm("Replace the mod's roster?", $"{project.Name} already has a roster. Copying this one replaces it, along with any edits you made to it.", "Replace it"))
        {
            return;
        }

        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);
            byte[]? icon = source.IconPath is { } iconPath && File.Exists(iconPath) ? await File.ReadAllBytesAsync(iconPath) : null;
            Result<RosterFile> check = await Task.Run(() => RosterFile.OpenSave(data));
            if (!check.IsSuccess)
            {
                Dialogs.Tell("That roster can't be used", check.Error!);
                return;
            }

            Result<bool> set = project.SetRoster(data, icon);
            if (!set.IsSuccess)
            {
                Dialogs.Tell("The roster wasn't copied", set.Error!);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("The roster wasn't copied", ex.Message);
            return;
        }

        _main.Status = $"Copied {source.Title} into {project.Name}. The original wasn't changed.";
        await OpenAsync(new RosterSource(project.Name, string.Empty, project.RosterPath, false), isMine: true);
        await _main.RefreshProjectAsync();
    }

    [RelayCommand]
    private async Task RemoveFromModAsync()
    {
        if (_main.Project is not { } project || !IsEditing
            || !Dialogs.Confirm("Remove the roster from your mod?", $"This deletes the roster in {project.Name}, along with every edit made to it. Rosters in the game and in other mods aren't touched.", "Remove it"))
        {
            return;
        }

        await _saveTask;
        Result<bool> removed = project.RemoveRoster();
        if (!removed.IsSuccess)
        {
            Dialogs.Tell("The roster wasn't removed", removed.Error!);
            return;
        }

        _loadedKey = null;
        await OpenBestAsync();
        await _main.RefreshProjectAsync();
    }

    /// <summary>Builds the save away from the page. Edits made meanwhile trigger one more pass.</summary>
    private void QueueSave()
    {
        if (_saving)
        {
            _saveAgain = true;
            return;
        }

        _saving = true;
        _saveTask = SaveAsync();
    }

    private async Task SaveAsync()
    {
        ModProject? project = _main.Project;
        RosterFile? roster = _roster;
        try
        {
            do
            {
                _saveAgain = false;
                if (IsEditing)
                {
                    roster = _roster;
                }

                if (project is null || roster is null)
                {
                    return;
                }

                byte[] database = roster.DatabaseBytes();
                Result<byte[]> built = await Task.Run(() => roster.BuildSave(database));
                Result<bool> written = built.IsSuccess ? project.UpdateRoster(built.Value!) : Result.Failure<bool>(built.Error!);
                if (!written.IsSuccess)
                {
                    _main.Status = $"The roster wasn't saved: {written.Error}";
                    Problem = written.Error;
                    return;
                }
            }
            while (_saveAgain);

            _loadedKey = KeyForCurrentMod();
            _main.Status = $"{_savedNote} Saved to {project!.Name}.".TrimStart();
            _savedNote = string.Empty;
        }
        finally
        {
            _saving = false;
        }

        await _main.RefreshProjectAsync();
    }

    private string? KeyForCurrentMod() => _main.Project is { HasRoster: true } project ? project.RosterPath : null;

    private static int? ParseHeight(string text)
    {
        Match match = Regex.Match(text.Trim(), "^(\\d)\\s*['’ ]\\s*(\\d{1,2})\\s*(\"|”)?$");
        if (match.Success)
        {
            int inches = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return inches < 12 ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 12) + inches : null;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int total) ? total : null;
    }

    private static SolidColorBrush MakeFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
