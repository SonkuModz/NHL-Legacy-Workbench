using System.Globalization;
using System.Text.RegularExpressions;
using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Sounds;

/// <summary>A sound bank, named for what's in it where that can be worked out.</summary>
/// <param name="Path">The game path of the bank.</param>
/// <param name="Title">What people would call it, such as a team's name for its goal horn.</param>
/// <param name="Group">The heading it goes under.</param>
/// <param name="Teams">The teams whose arena uses it, for searching.</param>
public sealed record SoundBankInfo(string Path, string Title, string Group, IReadOnlyList<string> Teams)
{
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The .ast stream file the bank's streamed sounds are in, if it has any.</summary>
    public string StreamPath => System.IO.Path.ChangeExtension(Path, ".ast").Replace('\\', '/');
}

/// <summary>
/// Sorts the game's sound banks into something browsable. A numbered bank belongs to whichever arenas name that number:
/// exhibitionarena has goalhorn for NNN_goal.abk, periodhorn for NNN_period.abk and chantaudioid for NNN_chant.abk.
/// </summary>
public static class SoundCatalog
{
    public const string GoalHorns = "Goal horns";
    public const string PeriodHorns = "Period horns";
    public const string Chants = "Team chants";
    public const string GoalieChants = "Goalie chants";
    public const string Crowd = "Crowd and rink";
    public const string Coach = "Coaching";
    public const string Menus = "Menus and loading";
    public const string Other = "Everything else";

    private static readonly string[] GroupOrder = [GoalHorns, PeriodHorns, Chants, GoalieChants, Crowd, Coach, Menus, Other];

    public static int OrderOf(string group) => Array.IndexOf(GroupOrder, group);

    public static IReadOnlyList<SoundBankInfo> Build(GameFileIndex index, TeamCatalog? teams)
    {
        Dictionary<(string Kind, int Id), List<string>> users = ArenaUsers(index, teams);
        var banks = new List<SoundBankInfo>();
        foreach (GameFile file in index.Files)
        {
            if (file.Path.EndsWith(".abk", StringComparison.Ordinal))
            {
                banks.Add(Describe(file.Path, users));
            }
        }

        return banks
            .OrderBy(b => OrderOf(b.Group))
            .ThenBy(b => b.Teams.Count == 0)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static SoundBankInfo Describe(string path, Dictionary<(string Kind, int Id), List<string>> users)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        Match numbered = Regex.Match(name, "^(\\d+)_(goal|period|chant)$");
        if (numbered.Success)
        {
            string digits = numbered.Groups[1].Value;
            int id = int.Parse(digits, CultureInfo.InvariantCulture);
            string kind = numbered.Groups[2].Value;
            string group = kind switch
            {
                "goal" => GoalHorns,
                "period" => PeriodHorns,
                _ => digits.Length > 3 ? GoalieChants : Chants,
            };

            IReadOnlyList<string> teams = digits.Length <= 3 && users.TryGetValue((kind, id), out List<string>? found) ? found : [];
            string title = teams.Count switch
            {
                0 => kind switch
                {
                    "goal" => $"Horn {digits}",
                    "period" => $"Period horn {digits}",
                    _ => digits.Length > 3 ? $"Goalie chant {digits}" : $"Chant {digits}",
                },
                1 => teams[0],
                _ => $"{teams[0]} and {teams.Count - 1} more",
            };

            return new SoundBankInfo(path, title, group, teams);
        }

        string lower = name.ToLowerInvariant();
        string other =
            lower.StartsWith("crowd", StringComparison.Ordinal) || lower.Contains("ambient", StringComparison.Ordinal) || lower.Contains("rink", StringComparison.Ordinal) ? Crowd
            : lower.StartsWith("bap_", StringComparison.Ordinal) || lower.Contains("coach", StringComparison.Ordinal) ? Coach
            : lower is "fe" or "hut_fe" or "controlcenter" || lower.StartsWith("load", StringComparison.Ordinal) ? Menus
            : Other;
        return new SoundBankInfo(path, name, other, []);
    }

    /// <summary>Which teams' arenas use which numbers. If the database can't be read, banks just go by number.</summary>
    private static Dictionary<(string Kind, int Id), List<string>> ArenaUsers(GameFileIndex index, TeamCatalog? teams)
    {
        var users = new Dictionary<(string, int), List<string>>();
        GameFile? file = index.Find("db/nhlng.db");
        if (file is null || teams is null)
        {
            return users;
        }

        Result<byte[]> data = index.ReadDecoded(file.Winner);
        Result<GameDatabase> opened = data.IsSuccess ? GameDatabase.Open(data.Value!) : Result.Failure<GameDatabase>(data.Error!);
        if (!opened.IsSuccess)
        {
            return users;
        }

        DatabaseTable? arenas = opened.Value!.FindTable("exhibitionarena");
        DatabaseTable? teamTable = opened.Value.FindTable("exhibitionteams");
        if (arenas is null || teamTable is null
            || arenas.FindColumn("index") is not { } arenaIndex || teamTable.FindColumn("index") is not { } teamIndex || teamTable.FindColumn("arenaid") is not { } arenaId)
        {
            return users;
        }

        var arenaOf = new Dictionary<string, int>();
        for (int row = 0; row < arenas.RowCount; row++)
        {
            arenaOf.TryAdd(arenas.GetCell(row, arenaIndex), row);
        }

        var rowOf = new Dictionary<string, int>();
        for (int row = 0; row < teamTable.RowCount; row++)
        {
            rowOf.TryAdd(teamTable.GetCell(row, teamIndex), row);
        }

        foreach (GameTeam team in teams.Teams.Where(t => t.HasDatabaseRow))
        {
            if (!rowOf.TryGetValue(team.Index.ToString(CultureInfo.InvariantCulture), out int teamRow)
                || !arenaOf.TryGetValue(teamTable.GetCell(teamRow, arenaId), out int arenaRow))
            {
                continue;
            }

            foreach ((string kind, string column) in new[] { ("goal", "goalhorn"), ("period", "periodhorn"), ("chant", "chantaudioid") })
            {
                if (arenas.FindColumn(column) is { } field && int.TryParse(arenas.GetCell(arenaRow, field), out int id))
                {
                    if (!users.TryGetValue((kind, id), out List<string>? list))
                    {
                        users[(kind, id)] = list = [];
                    }

                    if (!list.Contains(team.Name))
                    {
                        list.Add(team.Name);
                    }
                }
            }
        }

        return users;
    }
}
