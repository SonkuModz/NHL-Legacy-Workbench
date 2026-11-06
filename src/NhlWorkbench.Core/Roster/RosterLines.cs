using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Roster;

/// <summary>A place in a unit, named by the one-bit field that marks who is in it.</summary>
public sealed record RosterLineSpot(string Field, string Label);

/// <summary>
/// A group of spots that take the ice together. A player can hold only one spot within the units that share a
/// <see cref="Group"/>: nobody plays on two even-strength lines, but the same player can be on a line and the power play.
/// </summary>
public sealed record RosterLineUnit(string Section, string Title, string Group, IReadOnlyList<RosterLineSpot> Spots)
{
    public bool IsGoalies => Group == "goalies";
}

public sealed partial class RosterFile
{
    public const string EvenStrength = "Even strength";
    public const string PowerPlay = "Power play";
    public const string PenaltyKill = "Penalty kill";
    public const string Overtime = "Overtime";
    public const string Shootout = "Shootout";

    /// <summary>Every unit a team has, in the order a coach would read them.</summary>
    public static readonly IReadOnlyList<RosterLineUnit> LineUnits = MakeLineUnits();

    private static List<RosterLineUnit> MakeLineUnits()
    {
        var units = new List<RosterLineUnit>();
        void Add(string section, string title, string group, params (string Field, string Label)[] spots) =>
            units.Add(new RosterLineUnit(section, title, group, spots.Select(s => new RosterLineSpot(s.Field, s.Label)).ToList()));

        for (int n = 1; n <= 4; n++)
        {
            Add(EvenStrength, $"Line {n}", "even", ($"l{n}lw", "LW"), ($"l{n}c", "C"), ($"l{n}rw", "RW"));
        }

        for (int n = 1; n <= 3; n++)
        {
            Add(EvenStrength, $"Pair {n}", "even", ($"l{n}ld", "LD"), ($"l{n}rd", "RD"));
        }

        Add(EvenStrength, "Goalies", "goalies", ("g1", "Starter"), ("g2", "Backup"));

        for (int n = 1; n <= 2; n++)
        {
            Add(PowerPlay, $"Power play {n}", $"pp{n}", ($"pp{n}lw", "LW"), ($"pp{n}c", "C"), ($"pp{n}rw", "RW"), ($"pp{n}ld", "LD"), ($"pp{n}rd", "RD"));
        }

        for (int n = 1; n <= 2; n++)
        {
            Add(PowerPlay, $"Four on three {n}", $"pp4_{n}", ($"pp4_{n}lw", "LW"), ($"pp4_{n}c", "C"), ($"pp4_{n}ld", "LD"), ($"pp4_{n}rd", "RD"));
        }

        for (int n = 1; n <= 2; n++)
        {
            Add(PenaltyKill, $"Four skaters {n}", $"pk4_{n}", ($"pk4_{n}lw", "LW"), ($"pk4_{n}c", "C"), ($"pk4_{n}ld", "LD"), ($"pk4_{n}rd", "RD"));
        }

        for (int n = 1; n <= 2; n++)
        {
            Add(PenaltyKill, $"Three skaters {n}", $"pk3_{n}", ($"pk3_{n}c", "C"), ($"pk3_{n}ld", "LD"), ($"pk3_{n}rd", "RD"));
        }

        for (int n = 1; n <= 3; n++)
        {
            Add(Overtime, $"Overtime {n}", $"ot_{n}", ($"ot_{n}lw", "LW"), ($"ot_{n}c", "C"), ($"ot_{n}ld", "LD"), ($"ot_{n}rd", "RD"));
        }

        Add(Shootout, "Shooters", "shootout", ("s1", "First"), ("s2", "Second"), ("s3", "Third"), ("s4", "Fourth"), ("s5", "Fifth"));
        Add(Shootout, "Extra attackers", "extra", ("x1", "First"), ("x2", "Second"));
        return units;
    }

    /// <summary>The units this roster's table has fields for. Older files can lack some.</summary>
    public IReadOnlyList<RosterLineUnit> LineUnitsHere() =>
        LineUnits.Where(u => u.Spots.All(s => _slots.HasField(s.Field))).ToList();

    /// <summary>Who holds each spot on a team. Spots nobody holds are left out.</summary>
    public IReadOnlyDictionary<string, RosterPlayer> LineupOf(int team)
    {
        var lineup = new Dictionary<string, RosterPlayer>();
        List<RosterLineSpot> spots = LineUnitsHere().SelectMany(u => u.Spots).ToList();
        foreach (RosterPlayer player in PlayersOn(team))
        {
            if (player.SlotRow is { } slot)
            {
                foreach (RosterLineSpot spot in spots)
                {
                    if (_slots.GetInt(slot, spot.Field) != 0)
                    {
                        lineup.TryAdd(spot.Field, player);
                    }
                }
            }
        }

        return lineup;
    }

    /// <summary>
    /// Puts a player in a spot, or empties it when <paramref name="player"/> is null. If they already held a spot in the
    /// same group, whoever is being replaced takes that spot, so a move never leaves a hole.
    /// </summary>
    public Result<bool> SetLineSpot(int team, string field, RosterPlayer? player)
    {
        if (!IsEditable)
        {
            return Result.Failure<bool>("This roster is read-only.");
        }

        RosterLineUnit? unit = LineUnitsHere().FirstOrDefault(u => u.Spots.Any(s => s.Field == field));
        if (unit is null)
        {
            return Result.Failure<bool>("This roster has no such spot.");
        }

        if (player is not null)
        {
            if (player.SlotRow is null || player.Team != team)
            {
                return Result.Failure<bool>($"{player.FullName} isn't on this team.");
            }

            if (player.IsGoalie != unit.IsGoalies)
            {
                return Result.Failure<bool>(unit.IsGoalies ? "Only a goalie can go in net." : "A goalie can't play out.");
            }
        }

        IReadOnlyDictionary<string, RosterPlayer> lineup = LineupOf(team);
        lineup.TryGetValue(field, out RosterPlayer? replaced);
        if (player is null ? replaced is null : replaced?.SlotRow == player.SlotRow)
        {
            return Result.Success(true);
        }

        string? former = player is null
            ? null
            : LineUnitsHere().Where(u => u.Group == unit.Group).SelectMany(u => u.Spots).Select(s => s.Field)
                .FirstOrDefault(f => f != field && lineup.TryGetValue(f, out RosterPlayer? holder) && holder.SlotRow == player.SlotRow);

        return Finish(Run(
            () => former is null ? null : ClearSpot(team, former),
            () => ClearSpot(team, field),
            () => player is null ? null : FillSpot(player.SlotRow!.Value, field),
            () => former is not null && replaced is not null ? FillSpot(replaced.SlotRow!.Value, former) : null));
    }

    /// <summary>
    /// Sets the whole lineup from the ratings: lines by Overall and position, then special teams built from them. Extra
    /// attackers are left as they are, because nothing here knows how the game picks them.
    /// </summary>
    public Result<bool> SetBestLines(int team)
    {
        if (!IsEditable)
        {
            return Result.Failure<bool>("This roster is read-only.");
        }

        if (team == FreeAgents)
        {
            return Result.Failure<bool>("Free agents don't have lines.");
        }

        IReadOnlyList<RosterPlayer> everyone = PlayersOn(team);
        List<RosterPlayer> byOverall = everyone.OrderByDescending(p => p.Overall).ToList();
        var plan = new Dictionary<string, RosterPlayer?>();

        List<RosterPlayer> forwards = byOverall.Where(p => p.Position is 0 or 1 or 2).ToList();
        var lines = new (RosterPlayer? Lw, RosterPlayer? C, RosterPlayer? Rw)[4];
        for (int n = 0; n < 4; n++)
        {
            RosterPlayer? center = TakeBest(forwards, 0);
            RosterPlayer? left = TakeBest(forwards, 1);
            RosterPlayer? right = TakeBest(forwards, 2);
            lines[n] = (left, center, right);
            plan[$"l{n + 1}c"] = center;
            plan[$"l{n + 1}lw"] = left;
            plan[$"l{n + 1}rw"] = right;
        }

        List<RosterPlayer> defense = byOverall.Where(p => p.Position == 3).ToList();
        var pairs = new (RosterPlayer? Left, RosterPlayer? Right)[3];
        for (int n = 0; n < 3; n++)
        {
            pairs[n] = MakePair(defense);
            plan[$"l{n + 1}ld"] = pairs[n].Left;
            plan[$"l{n + 1}rd"] = pairs[n].Right;
        }

        List<RosterPlayer> goalies = byOverall.Where(p => p.IsGoalie).ToList();
        plan["g1"] = goalies.ElementAtOrDefault(0);
        plan["g2"] = goalies.ElementAtOrDefault(1);

        for (int n = 0; n < 2; n++)
        {
            plan[$"pp{n + 1}lw"] = lines[n].Lw;
            plan[$"pp{n + 1}c"] = lines[n].C;
            plan[$"pp{n + 1}rw"] = lines[n].Rw;
            plan[$"pp{n + 1}ld"] = pairs[n].Left;
            plan[$"pp{n + 1}rd"] = pairs[n].Right;
            plan[$"pp4_{n + 1}lw"] = lines[n].Lw;
            plan[$"pp4_{n + 1}c"] = lines[n].C;
            plan[$"pp4_{n + 1}ld"] = pairs[n].Left;
            plan[$"pp4_{n + 1}rd"] = pairs[n].Right;
        }

        for (int n = 0; n < 3; n++)
        {
            plan[$"ot_{n + 1}lw"] = lines[n].Lw;
            plan[$"ot_{n + 1}c"] = lines[n].C;
            plan[$"ot_{n + 1}ld"] = pairs[n].Left;
            plan[$"ot_{n + 1}rd"] = pairs[n].Right;
        }

        // The penalty kill goes to whoever defends best, whatever line they're on.
        List<RosterPlayer> killers = everyone.Where(p => p.Position is 0 or 1 or 2).OrderByDescending(p => RatingOf(p, "c_defawareness")).ToList();
        List<RosterPlayer> killerD = everyone.Where(p => p.Position == 3).OrderByDescending(p => RatingOf(p, "c_defawareness")).ToList();
        for (int n = 1; n <= 2; n++)
        {
            RosterPlayer? center = TakeBest(killers, 0);
            RosterPlayer? wing = TakeBest(killers, 1);
            (RosterPlayer? left, RosterPlayer? right) = MakePair(killerD);
            plan[$"pk4_{n}c"] = center;
            plan[$"pk4_{n}lw"] = wing;
            plan[$"pk4_{n}ld"] = left;
            plan[$"pk4_{n}rd"] = right;
            plan[$"pk3_{n}c"] = center;
            plan[$"pk3_{n}ld"] = left;
            plan[$"pk3_{n}rd"] = right;
        }

        List<RosterPlayer> shooters = everyone.Where(p => !p.IsGoalie)
            .OrderByDescending(p => RatingOf(p, "c_deking") + RatingOf(p, "c_wristaccuracy") + RatingOf(p, "c_puckcontrol")).ToList();
        for (int n = 0; n < 5; n++)
        {
            plan[$"s{n + 1}"] = shooters.ElementAtOrDefault(n);
        }

        return Finish(Run(plan.Where(p => _slots.HasField(p.Key)).Select<KeyValuePair<string, RosterPlayer?>, Func<string?>>(p => () =>
            ClearSpot(team, p.Key) ?? (p.Value?.SlotRow is { } slot ? FillSpot(slot, p.Key) : null)).ToArray()));
    }

    /// <summary>What's wrong with a team's lineup, in plain words. Empty when every even-strength spot and net is filled.</summary>
    public IReadOnlyList<string> LineProblems(int team)
    {
        var problems = new List<string>();
        if (team == FreeAgents)
        {
            return problems;
        }

        IReadOnlyDictionary<string, RosterPlayer> lineup = LineupOf(team);
        foreach (RosterLineUnit unit in LineUnitsHere().Where(u => u.Section == EvenStrength))
        {
            List<string> empty = unit.Spots.Where(s => !lineup.ContainsKey(s.Field)).Select(s => s.Label).ToList();
            if (empty.Count > 0)
            {
                problems.Add($"{unit.Title} has nobody at {string.Join(", ", empty)}.");
            }
        }

        foreach (IGrouping<long?, RosterPlayer> twice in lineup
            .Where(p => LineUnitsHere().Any(u => u.Group == "even" && u.Spots.Any(s => s.Field == p.Key)))
            .Select(p => p.Value)
            .GroupBy(p => (long?)p.SlotRow)
            .Where(g => g.Count() > 1))
        {
            problems.Add($"{twice.First().FullName} is on more than one even-strength line.");
        }

        return problems;
    }

    /// <summary>Takes the best player of a position from the list, or the best left if nobody plays it.</summary>
    private static RosterPlayer? TakeBest(List<RosterPlayer> pool, int position)
    {
        RosterPlayer? player = pool.FirstOrDefault(p => p.Position == position) ?? pool.FirstOrDefault();
        if (player is not null)
        {
            pool.Remove(player);
        }

        return player;
    }

    /// <summary>The two best from the list, each on the side of their shot where that can be done.</summary>
    private static (RosterPlayer? Left, RosterPlayer? Right) MakePair(List<RosterPlayer> pool)
    {
        RosterPlayer? first = pool.FirstOrDefault();
        RosterPlayer? second = pool.ElementAtOrDefault(1);
        pool.RemoveRange(0, Math.Min(2, pool.Count));
        return first?.Handedness == 1 || second?.Handedness == 0 && first?.Handedness != 0 ? (second, first) : (first, second);
    }

    private string? ClearSpot(int team, string field)
    {
        for (int row = 0; row < _slots.RowCount; row++)
        {
            if (_slots.GetInt(row, "team") == team && _slots.GetInt(row, field) != 0 && !_slots.TrySetInt(row, field, 0, out string? error))
            {
                return error;
            }
        }

        return null;
    }

    private string? FillSpot(int slot, string field) => _slots.TrySetInt(slot, field, 1, out string? error) ? null : error;

    private static Result<bool> Finish(string? error) => error is null ? Result.Success(true) : Result.Failure<bool>(error);
}
