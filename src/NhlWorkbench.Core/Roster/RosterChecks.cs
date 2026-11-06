namespace NhlWorkbench.Core.Roster;

public enum RosterIssueKind
{
    Lines,
    Numbers,
    Letters,
    TeamSize,
    Ratings,
}

/// <summary>Something on a team worth a look. None of these stop a roster being saved.</summary>
public sealed record RosterIssue(int Team, RosterIssueKind Kind, string Text);

public sealed partial class RosterFile
{
    private const int LowestOverall = 36;
    private const int ForwardsNeeded = 12;
    private const int DefenseNeeded = 6;
    private const int GoaliesNeeded = 2;

    /// <summary>What looks wrong with a team, from what a season's lines need and what the rows hold.</summary>
    public IReadOnlyList<RosterIssue> CheckTeam(int team)
    {
        var issues = new List<RosterIssue>();
        if (team == FreeAgents)
        {
            return issues;
        }

        IReadOnlyList<RosterPlayer> players = PlayersOn(team);
        foreach (string problem in LineProblems(team))
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.Lines, problem));
        }

        foreach (IGrouping<int, RosterPlayer> same in players.GroupBy(p => p.Jersey).Where(g => g.Count() > 1).OrderBy(g => g.Key))
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.Numbers, $"{string.Join(" and ", same.Select(p => p.FullName))} {(same.Count() == 2 ? "both" : "all")} wear {same.Key}."));
        }

        int captains = players.Count(p => p.Captain == Captain);
        if (captains != 1)
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.Letters, captains == 0 ? "Nobody wears the C." : $"{captains} players wear the C."));
        }

        int goalies = players.Count(p => p.IsGoalie);
        int defense = players.Count(p => !p.IsGoalie && p.PositionName == "D");
        int forwards = players.Count - goalies - defense;
        if (forwards < ForwardsNeeded)
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.TeamSize, $"Only {forwards} forwards, and four lines take {ForwardsNeeded}."));
        }

        if (defense < DefenseNeeded)
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.TeamSize, $"Only {defense} on defense, and three pairs take {DefenseNeeded}."));
        }

        if (goalies < GoaliesNeeded)
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.TeamSize, goalies == 0 ? "No goalies." : "Only one goalie, with no backup."));
        }

        int lowest = players.Count(p => p.Overall <= LowestOverall);
        if (lowest > 0)
        {
            issues.Add(new RosterIssue(team, RosterIssueKind.Ratings, $"{lowest} {(lowest == 1 ? "player shows" : "players show")} the lowest overall, {LowestOverall}. Their other ratings may be fine."));
        }

        return issues;
    }
}
