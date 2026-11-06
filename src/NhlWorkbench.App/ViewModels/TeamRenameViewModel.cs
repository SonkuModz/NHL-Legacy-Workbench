using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.App.ViewModels;

/// <summary>The Teams page's rename panel, which changes every string that names a team, in every language.</summary>
public partial class TeamsViewModel
{
    private IReadOnlyList<TeamNamePart> _nameParts = [];

    [ObservableProperty]
    private bool _isRenameOpen;

    [ObservableProperty]
    private string _renameFull = string.Empty;

    [ObservableProperty]
    private string _renameCity = string.Empty;

    [ObservableProperty]
    private string _renameNickname = string.Empty;

    [ObservableProperty]
    private string _renameShort = string.Empty;

    [ObservableProperty]
    private string _renameAbbreviation = string.Empty;

    [ObservableProperty]
    private string _renameNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameProblem))]
    private string? _renameProblem;

    public bool HasRenameProblem => RenameProblem is not null;

    private static IEnumerable<int> NumbersFor(GameTeam team) => new[] { team.Index, team.ArtId }.Where(n => n >= 0);

    [RelayCommand]
    public async Task OpenRenameAsync()
    {
        if (SelectedTeam?.Team is not { } team || _main.Index is not { } index)
        {
            return;
        }

        RenameProblem = null;
        await _main.Text.WaitUntilReadyAsync();
        ModProject? project = _main.Project;
        Result<TdbLanguageFile> english = await Task.Run(() => GameTextViewModel.ReadLanguage(index, project, "fe/loc/nhl_eng_us.db"));
        if (!english.IsSuccess)
        {
            RenameProblem = english.Error;
            return;
        }

        _nameParts = TeamNames.Read(english.Value!, team.Abbreviation, NumbersFor(team));
        string Text(string label) => _nameParts.FirstOrDefault(p => p.Label == label)?.Text ?? string.Empty;
        RenameFull = Text(TeamNames.FullName);
        RenameCity = Text(TeamNames.City);
        RenameNickname = Text(TeamNames.Nickname);
        RenameShort = Text(TeamNames.ShortNickname);
        RenameAbbreviation = Text(TeamNames.Abbreviation);
        int strings = _nameParts.Sum(p => p.Keys.Count);
        RenameNote = strings == 0
            ? $"The game's text has no strings for {team.Name}."
            : $"Changes {strings} strings in each language. The lists in this app keep the old name.";
        IsRenameOpen = true;
    }

    [RelayCommand]
    private void CloseRename() => IsRenameOpen = false;

    /// <summary>Writes what's changed in the boxes to every language.</summary>
    [RelayCommand]
    public async Task SaveRenameAsync()
    {
        if (SelectedTeam?.Team is not { } team || !_main.EnsureProject())
        {
            return;
        }

        if (RenameAbbreviation.Trim().Length is 0 or > TeamNames.LongestAbbreviation)
        {
            RenameProblem = $"An abbreviation needs 1 to {TeamNames.LongestAbbreviation} letters.";
            return;
        }

        var changes = new Dictionary<string, string>();
        void Compare(string label, string text)
        {
            if (_nameParts.FirstOrDefault(p => p.Label == label) is { } part && text.Trim().Length > 0 && text.Trim() != part.Text)
            {
                changes[label] = text.Trim();
            }
        }

        Compare(TeamNames.FullName, RenameFull);
        Compare(TeamNames.City, RenameCity);
        Compare(TeamNames.Nickname, RenameNickname);
        Compare(TeamNames.ShortNickname, RenameShort);
        Compare(TeamNames.Abbreviation, RenameAbbreviation);
        if (changes.Count == 0)
        {
            IsRenameOpen = false;
            return;
        }

        List<int> numbers = NumbersFor(team).ToList();
        _main.BusyText = $"Renaming {team.Name}";
        var problems = new List<string>();
        (int strings, int languages, string? error) = await _main.Text.ChangeEveryLanguageAsync((file, _) =>
        {
            (int changed, IReadOnlyList<string> bad) = TeamNames.Apply(file, TeamNames.Read(file, team.Abbreviation, numbers), changes);
            problems.AddRange(bad);
            return changed;
        });
        _main.BusyText = null;

        if (error is not null || problems.Count > 0)
        {
            RenameProblem = error ?? string.Join(" ", problems.Take(4));
            return;
        }

        IsRenameOpen = false;
        _main.Status = $"Renamed {team.Name}: {strings:N0} strings in {languages} languages.";
    }

    /// <summary>Puts every string naming this team back to the game's, in every language.</summary>
    [RelayCommand]
    public async Task PutBackNamesAsync()
    {
        if (SelectedTeam?.Team is not { } team || _main.Project is null)
        {
            return;
        }

        List<int> numbers = NumbersFor(team).ToList();
        _main.BusyText = $"Putting back {team.Name}";
        (int strings, _, string? error) = await _main.Text.ChangeEveryLanguageAsync((file, original) => TeamNames.PutBack(file, original, TeamNames.Read(original, team.Abbreviation, numbers)));
        _main.BusyText = null;
        if (error is not null)
        {
            RenameProblem = error;
            return;
        }

        IsRenameOpen = false;
        _main.Status = strings == 0 ? $"{team.Name} already has the game's names." : $"Put back {strings:N0} strings for {team.Name}.";
    }
}
