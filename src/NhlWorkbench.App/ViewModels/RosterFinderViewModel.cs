using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.Core.Roster;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A player found across the league.</summary>
public sealed record FinderRow(RosterPlayer Player, string Team, string League)
{
    public string Born => Player.BirthYear.ToString();

    public string Height => $"{Player.HeightInches / 12}'{Player.HeightInches % 12}\"";
}

/// <summary>Something to look at on a team.</summary>
public sealed record RosterIssueRow(RosterIssue Issue, string Team)
{
    public string Text => Issue.Text;
}

/// <summary>The Roster page's League and Checks tabs, which look across every team.</summary>
public partial class RosterViewModel
{
    private const string LeagueTab = "League";
    private const string ChecksTab = "Checks";
    private const string FreeAgentsScope = "free";
    private const int MostFound = 300;

    [ObservableProperty]
    private string _finderQuery = string.Empty;

    [ObservableProperty]
    private string _finderMinimum = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<FinderRow> _finderRows = [];

    [ObservableProperty]
    private string _finderSummary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<RosterIssueRow> _issueRows = [];

    [ObservableProperty]
    private string _issuesSummary = string.Empty;

    public IReadOnlyList<ToolChoice> FinderScopes { get; } =
        [new("NHL", "NHL") { IsActive = true }, new("Every league"), new("Free agents", FreeAgentsScope)];

    public IReadOnlyList<ToolChoice> FinderPositions { get; } =
        new[] { new ToolChoice("All") { IsActive = true } }.Concat(RosterFile.PositionNames.Select(p => new ToolChoice(p, p))).ToList();

    public IReadOnlyList<ToolChoice> FinderSorts { get; } =
        [new("Best", "overall") { IsActive = true }, new("Youngest", "young"), new("Oldest", "old"), new("Tallest", "tall"), new("Heaviest", "heavy")];

    public bool IsLeagueTab => Tab == LeagueTab;

    public bool IsChecksTab => Tab == ChecksTab;

    /// <summary>These tabs don't use the player panel, and want its room.</summary>
    public bool WantsWholeWidth => IsLinesTab || IsLeagueTab || IsChecksTab;

    partial void OnFinderQueryChanged(string value) => BuildFinder();

    partial void OnFinderMinimumChanged(string value) => BuildFinder();

    private IEnumerable<RosterTeamEntry> TeamsInScope()
    {
        string? scope = FinderScopes.First(s => s.IsActive).Value;
        return _entries.Where(e => scope switch
        {
            null => true,
            FreeAgentsScope => e.Index == RosterFile.FreeAgents,
            _ => e.League == scope,
        });
    }

    private void BuildFinder()
    {
        if (!IsLeagueTab)
        {
            return;
        }

        if (_roster is not { } roster)
        {
            FinderRows = [];
            FinderSummary = string.Empty;
            return;
        }

        string? position = FinderPositions.First(p => p.IsActive).Value;
        string[] words = FinderQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int minimum = int.TryParse(FinderMinimum, out int parsed) ? parsed : 0;
        IEnumerable<FinderRow> found = TeamsInScope()
            .SelectMany(team => roster.PlayersOn(team.Index).Select(p => new FinderRow(p, team.Name, team.League)))
            .Where(r => position is null || r.Player.PositionName == position)
            .Where(r => r.Player.Overall >= minimum)
            .Where(r => words.All(w => r.Player.FullName.Contains(w, StringComparison.OrdinalIgnoreCase)));

        List<FinderRow> sorted = (FinderSorts.First(s => s.IsActive).Value switch
        {
            "young" => found.OrderByDescending(r => (r.Player.BirthYear, r.Player.BirthMonth, r.Player.BirthDay)),
            "old" => found.OrderBy(r => (r.Player.BirthYear, r.Player.BirthMonth, r.Player.BirthDay)),
            "tall" => found.OrderByDescending(r => r.Player.HeightInches),
            "heavy" => found.OrderByDescending(r => r.Player.WeightPounds),
            _ => found.OrderByDescending(r => r.Player.Overall),
        }).ThenByDescending(r => r.Player.Overall).ThenBy(r => r.Player.LastName, StringComparer.OrdinalIgnoreCase).ToList();

        FinderRows = sorted.Take(MostFound).ToList();
        FinderSummary = sorted.Count > MostFound ? $"The first {MostFound} of {sorted.Count:N0} players." : sorted.Count == 1 ? "1 player." : $"{sorted.Count:N0} players.";
    }

    private void BuildIssues()
    {
        if (_roster is not { } roster)
        {
            IssueRows = [];
            IssuesSummary = string.Empty;
            return;
        }

        List<RosterTeamEntry> teams = TeamsInScope().Where(t => t.Index != RosterFile.FreeAgents).ToList();
        List<RosterIssueRow> rows = teams.SelectMany(t => roster.CheckTeam(t.Index).Select(i => new RosterIssueRow(i, t.Name))).ToList();
        int withIssues = rows.Select(r => r.Issue.Team).Distinct().Count();
        IssueRows = rows;
        IssuesSummary = rows.Count == 0
            ? $"Nothing to flag on these {teams.Count:N0} teams."
            : $"{rows.Count:N0} thing{(rows.Count == 1 ? string.Empty : "s")} to look at on {withIssues:N0} of {teams.Count:N0} teams. None of these stop the roster saving.";
    }

    [RelayCommand]
    private void ChooseFinderScope(ToolChoice choice)
    {
        Mark(FinderScopes, choice);
        RefreshTab();
    }

    [RelayCommand]
    private void ChooseFinderPosition(ToolChoice choice)
    {
        Mark(FinderPositions, choice);
        BuildFinder();
    }

    [RelayCommand]
    private void ChooseFinderSort(ToolChoice choice)
    {
        Mark(FinderSorts, choice);
        BuildFinder();
    }

    [RelayCommand]
    private void OpenFinderRow(FinderRow? row)
    {
        if (row is not null)
        {
            ShowPlayer(row.Player.Team, row.Player.PlayerId);
        }
    }

    [RelayCommand]
    private void OpenIssue(RosterIssueRow? row)
    {
        if (row is not null)
        {
            ShowTeam(row.Issue.Team);
            Tab = row.Issue.Kind == RosterIssueKind.Lines ? LinesTab : PlayersTab;
        }
    }
}
