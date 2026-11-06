using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Roster;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A choice in a box for a spot in the lines. A null player is an empty spot.</summary>
public sealed class LineOption(RosterPlayer? player)
{
    public RosterPlayer? Player { get; } = player;

    public string Label => Player is { } p ? $"{p.LastName} · {p.PositionName} {p.Overall}" : "Nobody";

    public string Full => Player is { } p ? $"{p.Jersey} {p.FullName}, {p.PositionName}, {p.Overall} overall" : "Nobody";

    public override string ToString() => Label;
}

/// <summary>A spot in a unit. Picking someone in the box puts them there straight away.</summary>
public sealed partial class LineSpotRow : ObservableObject
{
    private readonly RosterViewModel _owner;
    private LineOption? _selected;

    public LineSpotRow(RosterViewModel owner, RosterLineSpot spot, IReadOnlyList<LineOption> options, LineOption? selected)
    {
        _owner = owner;
        Spot = spot;
        Options = options;
        _selected = selected;
    }

    public RosterLineSpot Spot { get; }

    public IReadOnlyList<LineOption> Options { get; }

    public LineOption? Selected
    {
        get => _selected;
        set
        {
            if (value is not null && value != _selected)
            {
                _owner.CommitSpot(this, value);
            }

            OnPropertyChanged();
        }
    }
}

public sealed record LineUnitRow(string Title, IReadOnlyList<LineSpotRow> Spots);

/// <summary>A button in a row of choices that belongs to the page rather than to a player.</summary>
public sealed partial class ToolChoice(string label, string? value = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public string Label { get; } = label;

    public string? Value { get; } = value;
}

/// <summary>A line in the list of changes.</summary>
public sealed record ChangeRow(RosterChange Change, string Title, string Team)
{
    public string What => Change.What;

    public bool CanRevert => Change.PlayerId is not null;
}

/// <summary>The Roster page's tabs and tools: lines, undo, the list of changes, bulk ratings and CSV files.</summary>
public partial class RosterViewModel
{
    private const string PlayersTab = "Players";
    private const string LinesTab = "Lines";
    private const string ChangesTab = "Changes";

    private readonly RosterHistory _history = new();
    private byte[]? _lastBytes;
    private RosterFile? _originalRoster;
    private int _changesVersion;
    private Task _changesTask = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayersTab), nameof(IsLinesTab), nameof(IsChangesTab), nameof(IsLeagueTab), nameof(IsChecksTab), nameof(WantsWholeWidth))]
    private string _tab = PlayersTab;

    [ObservableProperty]
    private IReadOnlyList<LineUnitRow> _lineUnitRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLineNote))]
    private string _lineNote = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ChangeRow> _changeRows = [];

    [ObservableProperty]
    private string _changesSummary = string.Empty;

    [ObservableProperty]
    private bool _isBulkOpen;

    [ObservableProperty]
    private string _bulkAmount = "+2";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBulkProblem))]
    private string? _bulkProblem;

    public IReadOnlyList<ToolChoice> LineSections { get; } =
        new[] { RosterFile.EvenStrength, RosterFile.PowerPlay, RosterFile.PenaltyKill, RosterFile.Overtime, RosterFile.Shootout }
            .Select((s, i) => new ToolChoice(s) { IsActive = i == 0 }).ToList();

    public IReadOnlyList<ToolChoice> BulkScopes { get; } = [new("This team", "team") { IsActive = true }, new("The whole league", "league")];

    public IReadOnlyList<ToolChoice> BulkWho { get; } =
        [new("Everyone", "all") { IsActive = true }, new("Forwards", "forwards"), new("Defense", "defense"), new("Goalies", "goalies")];

    public IReadOnlyList<ToolChoice> BulkWhat { get; } =
        new[] { new ToolChoice("All ratings") { IsActive = true } }.Concat(RosterFile.RatingGroupNames.Select(g => new ToolChoice(g, g))).ToList();

    public bool IsPlayersTab => Tab == PlayersTab;

    public bool IsLinesTab => Tab == LinesTab;

    public bool IsChangesTab => Tab == ChangesTab;

    public bool HasLineNote => LineNote.Length > 0;

    public bool HasBulkProblem => BulkProblem is not null;

    public string UndoTip => _history.UndoDescription is { } what ? $"Undo {what}" : "Nothing to undo";

    public string RedoTip => _history.RedoDescription is { } what ? $"Redo {what}" : "Nothing to redo";

    partial void OnTabChanged(string value) => RefreshTab();

    /// <summary>For the checks in --shots mode, and for waiting on the list of changes.</summary>
    public Task WaitForChangesAsync() => _changesTask;

    public void ChooseLineSection(string section)
    {
        foreach (ToolChoice choice in LineSections)
        {
            choice.IsActive = choice.Label == section;
        }

        BuildLines();
    }

    /// <summary>Puts the rows on the current tab in step with the roster.</summary>
    private void RefreshTab()
    {
        if (IsLinesTab)
        {
            BuildLines();
        }
        else if (IsChangesTab)
        {
            _changesTask = BuildChangesAsync();
        }
        else if (IsLeagueTab)
        {
            BuildFinder();
        }
        else if (IsChecksTab)
        {
            BuildIssues();
        }
    }

    private void BuildLines()
    {
        if (_roster is not { } roster || SelectedTeam is not { } team)
        {
            LineUnitRows = [];
            LineNote = string.Empty;
            return;
        }

        if (team.Index == RosterFile.FreeAgents)
        {
            LineUnitRows = [];
            LineNote = "Free agents don't have lines. Pick a team.";
            return;
        }

        IReadOnlyList<RosterPlayer> players = roster.PlayersOn(team.Index);
        var nobody = new LineOption(null);
        List<LineOption> skaters = players.Where(p => !p.IsGoalie).OrderBy(GroupOrder).ThenByDescending(p => p.Overall).Select(p => new LineOption(p)).ToList();
        List<LineOption> goalies = players.Where(p => p.IsGoalie).OrderByDescending(p => p.Overall).Select(p => new LineOption(p)).ToList();
        List<LineOption> skaterChoices = [nobody, .. skaters];
        List<LineOption> goalieChoices = [nobody, .. goalies];

        IReadOnlyDictionary<string, RosterPlayer> lineup = roster.LineupOf(team.Index);
        string section = LineSections.First(s => s.IsActive).Label;
        LineUnitRows = roster.LineUnitsHere()
            .Where(u => u.Section == section)
            .Select(unit => new LineUnitRow(unit.Title, unit.Spots.Select(spot =>
            {
                List<LineOption> choices = unit.IsGoalies ? goalieChoices : skaterChoices;
                LineOption current = lineup.TryGetValue(spot.Field, out RosterPlayer? holder)
                    ? choices.First(o => o.Player?.SlotRow == holder.SlotRow)
                    : nobody;
                return new LineSpotRow(this, spot, choices, current);
            }).ToList()))
            .ToList();

        IReadOnlyList<string> problems = roster.LineProblems(team.Index);
        LineNote = problems.Count == 0 ? string.Empty : string.Join(" ", problems);
    }

    internal void CommitSpot(LineSpotRow row, LineOption option)
    {
        if (_roster is not { } roster || SelectedTeam is not { } team || !IsEditing)
        {
            return;
        }

        Result<bool> done = roster.SetLineSpot(team.Index, row.Spot.Field, option.Player);

        // Rebuilt once the box has finished closing, since the move can change other boxes too.
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            if (done.IsSuccess)
            {
                Changed($"the {team.Name} lines");
                BuildRows();
            }
            else
            {
                LineNote = done.Error!;
            }
        });
    }

    [RelayCommand]
    private void ShowTab(string tab) => Tab = tab;

    /// <summary>The team's logos and uniforms, on the Teams page.</summary>
    [RelayCommand]
    private void OpenTeamArt()
    {
        if (SelectedTeam?.Team is { } team)
        {
            _main.ShowTeamsCommand.Execute(null);
            _main.Teams.ShowTeam(team);
        }
    }

    [RelayCommand]
    private void ChooseSection(ToolChoice choice) => ChooseLineSection(choice.Label);

    [RelayCommand]
    private void BestLines()
    {
        if (_roster is not { } roster || SelectedTeam is not { } team || !IsEditing)
        {
            return;
        }

        Result<bool> done = roster.SetBestLines(team.Index);
        if (!done.IsSuccess)
        {
            LineNote = done.Error!;
            return;
        }

        _savedNote = $"Set the {team.Name} lines from the ratings.";
        Changed($"best lines for {team.Name}");
        BuildRows();
    }

    /// <summary>Everything that changes the roster finishes here, so it can be undone, saved and listed.</summary>
    private void Changed(string description)
    {
        if (_roster is not { } roster || _lastBytes is null)
        {
            return;
        }

        byte[] now = roster.DatabaseBytes();
        _history.Record(description, _lastBytes, now);
        _lastBytes = now;
        UndoChanged();
        QueueSave();
        if (IsChangesTab)
        {
            _changesTask = BuildChangesAsync();
        }
    }

    private void UndoChanged()
    {
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void ResetTools(bool isMine)
    {
        _history.Clear();
        _lastBytes = isMine ? _roster?.DatabaseBytes() : null;
        _originalRoster = null;
        ChangeRows = [];
        ChangesSummary = string.Empty;
        IsBulkOpen = false;
        UndoChanged();
    }

    private bool CanUndo() => IsEditing && _history.UndoDescription is not null;

    private bool CanRedo() => IsEditing && _history.RedoDescription is not null;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        string what = _history.UndoDescription ?? string.Empty;
        if (_lastBytes is not null && _history.Undo(_lastBytes) is { } bytes)
        {
            ShowBytes(bytes, $"Undid {what}.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        string what = _history.RedoDescription ?? string.Empty;
        if (_lastBytes is not null && _history.Redo(_lastBytes) is { } bytes)
        {
            ShowBytes(bytes, $"Redid {what}.");
        }
    }

    /// <summary>Makes these database bytes the roster on show, and saves them.</summary>
    private void ShowBytes(byte[] bytes, string note)
    {
        Result<RosterFile>? opened = _roster?.Reopened(bytes);
        if (opened is not { IsSuccess: true })
        {
            _main.Status = $"That didn't work: {opened?.Error}";
            return;
        }

        _roster = opened.Value;
        _lastBytes = bytes;
        _savedNote = note;
        UndoChanged();
        BuildRows();
        QueueSave();
        RefreshTab();
    }

    private RosterFile? OriginalRoster()
    {
        if (_originalRoster is null && _main.Project is { HasRoster: true } project)
        {
            Result<byte[]> bytes = project.ReadRosterOriginal();
            if (bytes.IsSuccess)
            {
                _originalRoster = RosterFile.OpenSave(bytes.Value!).Value;
            }
        }

        return _originalRoster;
    }

    private async Task BuildChangesAsync()
    {
        int version = ++_changesVersion;
        if (!IsEditing || _lastBytes is not { } bytes || _roster is not { } roster || OriginalRoster() is not { } original)
        {
            ChangeRows = [];
            ChangesSummary = IsEditing ? "The original roster couldn't be read, so changes can't be listed." : "Copy a roster into your mod, and what you change will be listed here.";
            return;
        }

        IReadOnlyList<RosterChange>? changes = await Task.Run(() =>
        {
            Result<RosterFile> copy = roster.Reopened(bytes);
            return copy.IsSuccess ? copy.Value!.ChangesSince(original) : null;
        });
        if (version != _changesVersion || changes is null)
        {
            return;
        }

        var names = _entries.ToDictionary(e => e.Index, e => e.Name);
        ChangeRows = changes.Select(c =>
        {
            string team = names.GetValueOrDefault(c.Team, c.Team == RosterFile.FreeAgents ? "Free agents" : $"Team {c.Team}");
            return new ChangeRow(c, c.PlayerId is null ? team : c.Name, c.PlayerId is null ? string.Empty : team);
        }).ToList();
        ChangesSummary = changes.Count == 0
            ? "Nothing has changed from the roster you started with."
            : $"{changes.Count} change{(changes.Count == 1 ? string.Empty : "s")} from the roster you started with.";
    }

    [RelayCommand]
    private void RevertPlayer(ChangeRow? row)
    {
        if (row?.Change.PlayerId is not { } id || _roster is not { } roster || OriginalRoster() is not { } original || !IsEditing)
        {
            return;
        }

        Result<bool> done = roster.RevertPlayer(original, id);
        if (!done.IsSuccess)
        {
            _main.Status = done.Error!;
            return;
        }

        _savedNote = $"Put {row.Title} back.";
        Changed($"putting {row.Title} back");
        BuildRows();
    }

    [RelayCommand]
    private void StartOver()
    {
        if (OriginalRoster() is not { } original || _lastBytes is null || !IsEditing
            || !Dialogs.Confirm("Go back to the roster you started with?", "Every change to this roster goes, including swaps and lines. Undo can still bring them back until you close the app.", "Go back"))
        {
            return;
        }

        byte[] bytes = original.DatabaseBytes();
        _history.Record("going back to the start", _lastBytes, bytes);
        ShowBytes(bytes, "Back to the roster you started with.");
    }

    [RelayCommand]
    private void ToggleBulk()
    {
        IsBulkOpen = !IsBulkOpen;
        BulkProblem = null;
    }

    [RelayCommand]
    private void ChooseBulkScope(ToolChoice choice) => Mark(BulkScopes, choice);

    [RelayCommand]
    private void ChooseBulkWho(ToolChoice choice) => Mark(BulkWho, choice);

    [RelayCommand]
    private void ChooseBulkWhat(ToolChoice choice) => Mark(BulkWhat, choice);

    private static void Mark(IReadOnlyList<ToolChoice> choices, ToolChoice picked)
    {
        foreach (ToolChoice choice in choices)
        {
            choice.IsActive = choice == picked;
        }
    }

    [RelayCommand]
    private void ApplyBulk()
    {
        if (_roster is not { } roster || SelectedTeam is not { } team || !IsEditing)
        {
            return;
        }

        if (!int.TryParse(BulkAmount.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int amount) || amount == 0)
        {
            BulkProblem = "Write how much to add or take away, like +2 or -3.";
            return;
        }

        bool league = BulkScopes.First(c => c.IsActive).Value == "league";
        IEnumerable<int> teams = league ? _entries.Where(e => e.League == team.League).Select(e => e.Index) : [team.Index];
        string? who = BulkWho.First(c => c.IsActive).Value;
        List<RosterPlayer> players = teams.SelectMany(roster.PlayersOn).Where(p => who switch
        {
            "forwards" => p.Position is 0 or 1 or 2,
            "defense" => p.Position == 3,
            "goalies" => p.IsGoalie,
            _ => true,
        }).ToList();

        string? group = BulkWhat.First(c => c.IsActive).Value;
        Result<int> done = roster.AdjustRatings(players, amount, group);
        if (!done.IsSuccess)
        {
            BulkProblem = done.Error;
            return;
        }

        if (done.Value == 0)
        {
            BulkProblem = "Nothing changed. Those players have no such ratings, or they're already at the limit.";
            return;
        }

        BulkProblem = null;
        string place = league ? team.League : team.Name;
        _savedNote = $"Changed {done.Value:N0} ratings on {players.DistinctBy(p => p.PlayerId).Count():N0} players in {place}.";
        Changed($"the ratings change for {place}");
        BuildRows();
    }

    [RelayCommand]
    private async Task ExportCsvAsync(string scope)
    {
        bool everything = scope == "all";
        string name = everything ? "roster" : SelectedTeam?.Name ?? "team";
        var dialog = new SaveFileDialog { Title = "Save the players as a CSV", FileName = $"{name}.csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() == true)
        {
            await SaveCsvAsync(dialog.FileName, everything);
        }
    }

    /// <summary>Writes the CSV from a copy, so edits can carry on while a big one is made.</summary>
    public async Task SaveCsvAsync(string path, bool everything)
    {
        if (_roster is not { } roster)
        {
            return;
        }

        List<int> teams = everything ? _entries.Select(e => e.Index).ToList() : SelectedTeam is { } team ? [team.Index] : [];
        var names = _entries.ToDictionary(e => e.Index, e => e.Name);
        byte[] bytes = roster.DatabaseBytes();
        _main.BusyText = "Writing the CSV";
        Result<bool> saved = await Task.Run(() =>
        {
            try
            {
                Result<RosterFile> copy = roster.Reopened(bytes);
                if (!copy.IsSuccess)
                {
                    return Result.Failure<bool>(copy.Error!);
                }

                // The byte order mark tells Excel it's UTF-8, or accented names come out wrong.
                File.WriteAllText(path, copy.Value!.ExportCsv(teams, t => names.GetValueOrDefault(t, string.Empty)), new UTF8Encoding(true));
                return Result.Success(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<bool>(ex.Message, ex);
            }
        });
        _main.BusyText = null;
        _main.Status = saved.IsSuccess ? $"Saved {path}" : $"The CSV wasn't saved: {saved.Error}";
    }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var dialog = new OpenFileDialog { Title = "Choose a roster CSV", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await ApplyCsvAsync(await File.ReadAllTextAsync(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("That file couldn't be read", ex.Message);
        }
    }

    /// <summary>Applies a CSV to a copy, and keeps the copy only if every value in it was good.</summary>
    public async Task ApplyCsvAsync(string csv)
    {
        if (_roster is not { } roster || _lastBytes is not { } before || !IsEditing)
        {
            return;
        }

        _main.BusyText = "Reading the CSV";
        (Result<RosterImportResult> Import, byte[]? Bytes) result = await Task.Run(() =>
        {
            Result<RosterFile> copy = roster.Reopened(before);
            if (!copy.IsSuccess)
            {
                return (Result.Failure<RosterImportResult>(copy.Error!), null);
            }

            Result<RosterImportResult> imported = copy.Value!.ImportCsv(csv);
            return (imported, imported.IsSuccess ? copy.Value.DatabaseBytes() : null);
        });
        _main.BusyText = null;

        if (!result.Import.IsSuccess)
        {
            Dialogs.Tell("Nothing was changed", result.Import.Error!);
            return;
        }

        RosterImportResult import = result.Import.Value!;
        string skipped = import.Skipped.Count > 0 ? $" {import.Skipped.Count} line{(import.Skipped.Count == 1 ? " was" : "s were")} skipped because the player isn't on that team here." : string.Empty;
        string note = $"The CSV changed {import.Values:N0} values on {import.Players:N0} players.{skipped}";
        if (import.Values == 0)
        {
            _main.Status = note;
            return;
        }

        _history.Record("the CSV import", before, result.Bytes!);
        ShowBytes(result.Bytes!, note);
    }
}
