using System.Globalization;
using System.Text;
using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Roster;

/// <summary>Something that differs from the roster as first added to the mod.</summary>
/// <param name="PlayerId">The player, or null for a change to a team, such as a swap or its lines.</param>
/// <param name="Name">The player's current name. Empty for a team's change.</param>
/// <param name="Team">The team it happened on, or <see cref="RosterFile.FreeAgents"/>.</param>
/// <param name="What">The change in words.</param>
public sealed record RosterChange(long? PlayerId, string Name, int Team, string What);

/// <summary>What an import did.</summary>
public sealed record RosterImportResult(int Players, int Values, IReadOnlyList<string> Skipped);

/// <summary>Tools that work across many players: bulk ratings, the list of changes, and CSV files.</summary>
public sealed partial class RosterFile
{
    private static readonly string[] CsvFixedColumns =
        ["Team", "TeamIndex", "PlayerId", "FirstName", "LastName", "Number", "Position", "Shoots", "HeightInches", "WeightPounds", "Born", "Birthplace", "Style", "Letter"];

    /// <summary>Every rating group name, skaters' first and then those only goalies have.</summary>
    public static IReadOnlyList<string> RatingGroupNames { get; } =
        SkaterRatings.Concat(GoalieRatings).Select(r => r.Group).Distinct().ToList();

    /// <summary>Opens these exact database bytes as a new roster that saves the same way as this one.</summary>
    public Result<RosterFile> Reopened(byte[] databaseBytes) => Open(databaseBytes, _container);

    /// <summary>
    /// Adds an amount to the ratings of these players, stopping at the lowest and highest a rating can be. A player listed
    /// twice, such as on a club and a national team, changes once. Returns how many values changed.
    /// </summary>
    /// <param name="group">One of <see cref="RatingGroupNames"/>, or null for every rating.</param>
    public Result<int> AdjustRatings(IEnumerable<RosterPlayer> players, int amount, string? group)
    {
        if (!IsEditable)
        {
            return Result.Failure<int>("This roster is read-only.");
        }

        int changed = 0;
        foreach (RosterPlayer player in players.DistinctBy(p => p.PlayerId))
        {
            foreach (RosterRating rating in (player.IsGoalie ? GoalieRatings : SkaterRatings).Where(r => group is null || r.Group == group))
            {
                int before = RatingOf(player, rating.Field);
                int after = Math.Clamp(before + amount, LowestRating, HighestRating);
                if (after != before)
                {
                    Result<bool> set = SetRating(player, rating.Field, after);
                    if (!set.IsSuccess)
                    {
                        return Result.Failure<int>($"{player.FullName}: {set.Error}");
                    }

                    changed++;
                }
            }
        }

        return Result.Success(changed);
    }

    /// <summary>Everything that differs from an earlier copy of this roster, players first and then teams.</summary>
    public IReadOnlyList<RosterChange> ChangesSince(RosterFile original)
    {
        var perPlayer = new Dictionary<long, List<string>>();
        var teams = new List<RosterChange>();

        foreach ((long id, int bio) in _bioRows)
        {
            if (original._bioRows.TryGetValue(id, out int oldBio))
            {
                var notes = new List<string>();
                CompareBio(original, bio, oldBio, notes);
                CompareRatings(original, id, notes);
                if (notes.Count > 0)
                {
                    perPlayer[id] = notes;
                }
            }
        }

        string[] lineFields = LineUnitsHere().SelectMany(u => u.Spots).Select(s => s.Field).ToArray();
        var lineChanges = new Dictionary<int, int>();
        var playerTeams = new Dictionary<long, int>();
        for (int row = 0; row < Math.Min(_slots.RowCount, original._slots.RowCount); row++)
        {
            int team = (int)_slots.GetInt(row, "team");
            long key = _slots.GetInt(row, "playerindex");
            long oldKey = original._slots.GetInt(row, "playerindex");
            if (!IsOpen(row) && _players.TryGetValue(key, out long id))
            {
                playerTeams.TryAdd(id, team);
            }

            if (key != oldKey)
            {
                teams.Add(new RosterChange(null, string.Empty, team, $"{original.NameOfKey(oldKey)} out, {NameOfKey(key)} in"));
            }
            else if (!IsOpen(row) && _players.TryGetValue(key, out long player))
            {
                long jersey = _slots.GetInt(row, "jersey");
                long oldJersey = original._slots.GetInt(row, "jersey");
                if (jersey != oldJersey)
                {
                    Note(perPlayer, player, $"Number {oldJersey} → {jersey}");
                }

                long captain = _slots.GetInt(row, "captain");
                long oldCaptain = original._slots.GetInt(row, "captain");
                if (captain != oldCaptain)
                {
                    Note(perPlayer, player, $"Letter {LetterName(oldCaptain)} → {LetterName(captain)}");
                }
            }

            int spots = lineFields.Count(f => _slots.GetInt(row, f) != original._slots.GetInt(row, f));
            if (spots > 0)
            {
                lineChanges[team] = lineChanges.GetValueOrDefault(team) + spots;
            }
        }

        foreach ((int team, int count) in lineChanges)
        {
            teams.Add(new RosterChange(null, string.Empty, team, "Lines changed"));
        }

        return perPlayer
            .Select(p => new RosterChange(p.Key, NameOfBio(_bioRows[p.Key]), playerTeams.GetValueOrDefault(p.Key, FreeAgents), string.Join(", ", p.Value.Distinct())))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(teams.OrderBy(c => c.Team))
            .ToList();
    }

    /// <summary>
    /// Puts one player's name, details, ratings, numbers and letters back as they were in the earlier copy. Which team
    /// they're on stays as it is, because undoing a swap would also move someone else.
    /// </summary>
    public Result<bool> RevertPlayer(RosterFile original, long playerId)
    {
        if (!IsEditable)
        {
            return Result.Failure<bool>("This roster is read-only.");
        }

        if (!_bioRows.TryGetValue(playerId, out int bio) || !original._bioRows.TryGetValue(playerId, out int oldBio))
        {
            return Result.Failure<bool>("That player isn't in the original roster.");
        }

        bool goalie = _bios.GetInt(bio, "position") == GoaliePosition;
        TdbTableViewPair ratings = goalie
            ? new(_goalies, original._goalies, _goalieRows, original._goalieRows)
            : new(_skaters, original._skaters, _skaterRows, original._skaterRows);
        if (!ratings.Rows.TryGetValue(playerId, out int row) || !ratings.OldRows.TryGetValue(playerId, out int oldRow))
        {
            return Result.Failure<bool>("That player has no ratings in the original roster.");
        }

        var steps = new List<Func<string?>>();
        foreach (string field in BioTextFields)
        {
            steps.Add(() => _bios.TrySetText(bio, field, original._bios.GetText(oldBio, field), out string? e) ? null : e);
        }

        foreach (string field in BioNumberFields)
        {
            steps.Add(() => _bios.TrySetInt(bio, field, original._bios.GetInt(oldBio, field), out string? e) ? null : e);
        }

        foreach (RosterRating rating in goalie ? GoalieRatings : SkaterRatings)
        {
            steps.Add(() => ratings.Table.TrySetInt(row, rating.Field, ratings.Old.GetInt(oldRow, rating.Field), out string? e) ? null : e);
        }

        long style = ratings.Old.GetInt(oldRow, "playerstyle");
        steps.Add(() => ratings.Table.TrySetInt(row, "playerstyle", style, out string? e) ? null : e);
        for (int slot = 0; slot < Math.Min(_slots.RowCount, original._slots.RowCount); slot++)
        {
            int thisSlot = slot;
            if (!IsOpen(slot) && _players.GetValueOrDefault(_slots.GetInt(slot, "playerindex"), -1) == playerId)
            {
                steps.Add(() => _slots.TrySetInt(thisSlot, "playerstyle", style, out string? e) ? null : e);
                if (original._slots.GetInt(slot, "playerindex") == _slots.GetInt(slot, "playerindex"))
                {
                    long jersey = original._slots.GetInt(slot, "jersey");
                    long captain = original._slots.GetInt(slot, "captain");
                    int team = (int)_slots.GetInt(slot, "team");
                    steps.Add(() => _slots.TrySetInt(thisSlot, "jersey", jersey, out string? e) ? null : e);
                    steps.Add(() => captain == Captain ? ClearCaptain(team) : null);
                    steps.Add(() => _slots.TrySetInt(thisSlot, "captain", captain, out string? e) ? null : e);
                }
            }
        }

        return Finish(Run(steps.ToArray()));
    }

    /// <summary>A CSV of these teams' players, for a spreadsheet. Changes come back through <see cref="ImportCsv"/>.</summary>
    public string ExportCsv(IEnumerable<int> teams, Func<int, string> teamName)
    {
        List<string> ratingColumns = SkaterRatings.Concat(GoalieRatings).Select(r => r.Label).Distinct().ToList();
        var text = new StringBuilder();
        text.AppendLine(string.Join(',', CsvFixedColumns.Concat(ratingColumns).Select(CsvText.Quote)));
        foreach (int team in teams)
        {
            foreach (RosterPlayer player in PlayersOn(team))
            {
                var labels = (player.IsGoalie ? GoalieRatings : SkaterRatings).ToDictionary(r => r.Label, r => r.Field);
                IEnumerable<string> cells = new[]
                {
                    teamName(team), Number(team), player.PlayerId.ToString(CultureInfo.InvariantCulture),
                    player.FirstName, player.LastName, Number(player.Jersey), player.PositionName, player.Handedness == 0 ? "Left" : "Right",
                    Number(player.HeightInches), Number(player.WeightPounds), $"{player.BirthYear:0000}-{player.BirthMonth:00}-{player.BirthDay:00}",
                    player.Birthplace, player.StyleName, player.IsFreeAgent ? string.Empty : LetterName(player.Captain),
                }.Concat(ratingColumns.Select(c => labels.TryGetValue(c, out string? field) ? Number(RatingOf(player, field)) : string.Empty));
                text.AppendLine(string.Join(',', cells.Select(CsvText.Quote)));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Applies a CSV made by <see cref="ExportCsv"/>. Players are found by TeamIndex and PlayerId, a blank cell leaves a value
    /// alone, and any column can be left out. It fails at the first bad value after writing some of them, so run it on a
    /// <see cref="Reopened"/> copy and keep the copy only if it succeeds.
    /// </summary>
    public Result<RosterImportResult> ImportCsv(string csv)
    {
        if (!IsEditable)
        {
            return Result.Failure<RosterImportResult>("This roster is read-only.");
        }

        List<string[]> rows = CsvText.Parse(csv);
        if (rows.Count == 0)
        {
            return Result.Failure<RosterImportResult>("That file is empty.");
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows[0].Length; i++)
        {
            columns.TryAdd(rows[0][i].Trim(), i);
        }

        if (!columns.ContainsKey("TeamIndex") || !columns.ContainsKey("PlayerId"))
        {
            return Result.Failure<RosterImportResult>("This file needs TeamIndex and PlayerId columns. Export a roster CSV to see the layout.");
        }

        var players = new Dictionary<int, Dictionary<long, RosterPlayer>>();
        var skipped = new List<string>();
        var jerseys = new List<(RosterPlayer Player, int Number)>();
        int changedPlayers = 0;
        int values = 0;
        for (int line = 1; line < rows.Count; line++)
        {
            string[] row = rows[line];
            string Cell(string name) => columns.TryGetValue(name, out int at) && at < row.Length ? row[at].Trim() : string.Empty;

            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (!int.TryParse(Cell("TeamIndex"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int team)
                || !long.TryParse(Cell("PlayerId"), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                return Result.Failure<RosterImportResult>($"Line {line + 1}: TeamIndex and PlayerId have to be numbers.");
            }

            if (!players.TryGetValue(team, out Dictionary<long, RosterPlayer>? onTeam))
            {
                onTeam = PlayersOn(team).GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.First());
                players[team] = onTeam;
            }

            if (!onTeam.TryGetValue(id, out RosterPlayer? player))
            {
                skipped.Add($"Line {line + 1}: player {id} isn't on team {team} in this roster.");
                continue;
            }

            int before = values;
            string? error = ImportPlayer(player, columns.Keys, Cell, jerseys, ref values);
            if (error is not null)
            {
                return Result.Failure<RosterImportResult>($"Line {line + 1} ({player.FullName}): {error}");
            }

            changedPlayers += values > before ? 1 : 0;
        }

        // Numbers go in together at the end, so two players can trade them in one file.
        foreach ((RosterPlayer player, int number) in jerseys)
        {
            Result<bool> set = WriteJersey(player, number);
            if (!set.IsSuccess)
            {
                return Result.Failure<RosterImportResult>($"{player.FullName}: {set.Error}");
            }
        }

        foreach (int team in jerseys.Select(j => j.Player.Team).Where(t => t != FreeAgents).Distinct())
        {
            if (PlayersOn(team).GroupBy(p => p.Jersey).FirstOrDefault(g => g.Count() > 1) is { } clash)
            {
                return Result.Failure<RosterImportResult>($"{string.Join(" and ", clash.Select(p => p.FullName))} would both wear {clash.Key}.");
            }
        }

        return Result.Success(new RosterImportResult(changedPlayers, values, skipped));
    }

    private string? ImportPlayer(RosterPlayer player, IEnumerable<string> columnNames, Func<string, string> cell, List<(RosterPlayer, int)> jerseys, ref int values)
    {
        int count = 0;
        string? Apply(Result<bool> result)
        {
            count++;
            return result.IsSuccess ? null : result.Error;
        }

        string first = cell("FirstName");
        string last = cell("LastName");
        if ((first.Length > 0 && first != player.FirstName) || (last.Length > 0 && last != player.LastName))
        {
            if (Apply(SetName(player, first.Length > 0 ? first : player.FirstName, last.Length > 0 ? last : player.LastName)) is { } e)
            {
                return e;
            }
        }

        string place = cell("Birthplace");
        if (place.Length > 0 && place != player.Birthplace && Apply(SetBirthplace(player, place)) is { } placeError)
        {
            return placeError;
        }

        string number = cell("Number");
        if (number.Length > 0)
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int jersey) || jersey > 99)
            {
                return $"'{number}' isn't a number from 0 to 99.";
            }

            if (jersey != player.Jersey)
            {
                jerseys.Add((player, jersey));
                count++;
            }
        }

        string position = cell("Position");
        if (position.Length > 0 && !position.Equals(player.PositionName, StringComparison.OrdinalIgnoreCase))
        {
            int at = PositionNames.ToList().FindIndex(p => p.Equals(position, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                return $"'{position}' isn't a position.";
            }

            if (Apply(SetPosition(player, at)) is { } e)
            {
                return e;
            }
        }

        string hand = cell("Shoots");
        if (hand.Length > 0)
        {
            int? value = hand.StartsWith('L') || hand.StartsWith('l') ? 0 : hand.StartsWith('R') || hand.StartsWith('r') ? 1 : null;
            if (value is null)
            {
                return $"'{hand}' isn't Left or Right.";
            }

            if (value != player.Handedness && Apply(SetHandedness(player, value.Value)) is { } e)
            {
                return e;
            }
        }

        string height = cell("HeightInches");
        if (height.Length > 0)
        {
            if (!int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out int inches))
            {
                return $"'{height}' isn't a height in inches.";
            }

            if (inches != player.HeightInches && Apply(SetHeight(player, inches)) is { } e)
            {
                return e;
            }
        }

        string weight = cell("WeightPounds");
        if (weight.Length > 0)
        {
            if (!int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out int pounds))
            {
                return $"'{weight}' isn't a weight in pounds.";
            }

            if (pounds != player.WeightPounds && Apply(SetWeight(player, pounds)) is { } e)
            {
                return e;
            }
        }

        string born = cell("Born");
        if (born.Length > 0)
        {
            // Spreadsheets like to rewrite dates, so any form that says the same day is fine.
            if (!DateOnly.TryParse(born, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                return $"'{born}' isn't a date.";
            }

            if ((date.Year, date.Month, date.Day) != (player.BirthYear, player.BirthMonth, player.BirthDay) && Apply(SetBirthDate(player, date.Year, date.Month, date.Day)) is { } e)
            {
                return e;
            }
        }

        string style = cell("Style");
        if (style.Length > 0 && !style.Equals(player.StyleName, StringComparison.OrdinalIgnoreCase))
        {
            RosterChoice? choice = (player.IsGoalie ? GoalieStyles : SkaterStyles).FirstOrDefault(s => s.Label.Equals(style, StringComparison.OrdinalIgnoreCase));
            if (choice is null)
            {
                return $"'{style}' isn't a style for this kind of player.";
            }

            if (Apply(SetStyle(player, choice.Value)) is { } e)
            {
                return e;
            }
        }

        string letter = cell("Letter");
        if (letter.Length > 0 && !player.IsFreeAgent && !letter.Equals(LetterName(player.Captain), StringComparison.OrdinalIgnoreCase))
        {
            int? value = letter.ToUpperInvariant() switch
            {
                "NONE" => NoCaptain,
                "C" or "CAPTAIN" => Captain,
                "A" or "ALTERNATE" => Alternate,
                _ => null,
            };
            if (value is null)
            {
                return $"'{letter}' isn't a letter. Use None, C or A.";
            }

            if (Apply(SetCaptain(player, value.Value)) is { } e)
            {
                return e;
            }
        }

        foreach (RosterRating rating in player.IsGoalie ? GoalieRatings : SkaterRatings)
        {
            string text = cell(rating.Label);
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return $"{rating.Label}: '{text}' isn't a number.";
            }

            if (value != RatingOf(player, rating.Field) && Apply(SetRating(player, rating.Field, value)) is { } e)
            {
                return $"{rating.Label}: {e}";
            }
        }

        values += count;
        return null;
    }

    private void CompareBio(RosterFile original, int bio, int oldBio, List<string> notes)
    {
        string name = NameOfBio(bio);
        string oldName = original.NameOfBio(oldBio);
        if (name != oldName)
        {
            notes.Add($"Name {oldName} → {name}");
        }

        string place = _bios.GetText(bio, "birthplace");
        string oldPlace = original._bios.GetText(oldBio, "birthplace");
        if (place != oldPlace)
        {
            notes.Add($"Birthplace {oldPlace} → {place}");
        }

        void Number(string field, string label, Func<long, string> show)
        {
            long now = _bios.GetInt(bio, field);
            long then = original._bios.GetInt(oldBio, field);
            if (now != then)
            {
                notes.Add($"{label} {show(then)} → {show(now)}");
            }
        }

        Number("position", "Position", v => PositionNames[Math.Clamp((int)v, 0, PositionNames.Count - 1)]);
        Number("handedness", "Shoots", v => v == 0 ? "left" : "right");
        Number("height", "Height", v => $"{(v + HeightOffset) / 12}'{(v + HeightOffset) % 12}\"");
        Number("weight", "Weight", v => (v + WeightOffset).ToString(CultureInfo.InvariantCulture));
        if ((_bios.GetInt(bio, "year"), _bios.GetInt(bio, "month"), _bios.GetInt(bio, "day")) != (original._bios.GetInt(oldBio, "year"), original._bios.GetInt(oldBio, "month"), original._bios.GetInt(oldBio, "day")))
        {
            notes.Add("Birth date");
        }
    }

    private void CompareRatings(RosterFile original, long id, List<string> notes)
    {
        bool goalie = _goalieRows.ContainsKey(id) && !_skaterRows.ContainsKey(id);
        (TdbTableView table, TdbTableView oldTable) = goalie ? (_goalies, original._goalies) : (_skaters, original._skaters);
        Dictionary<long, int> rows = goalie ? _goalieRows : _skaterRows;
        Dictionary<long, int> oldRows = goalie ? original._goalieRows : original._skaterRows;
        if (!rows.TryGetValue(id, out int row) || !oldRows.TryGetValue(id, out int oldRow))
        {
            return;
        }

        foreach (RosterRating rating in goalie ? GoalieRatings : SkaterRatings)
        {
            long now = table.GetInt(row, rating.Field);
            long then = oldTable.GetInt(oldRow, rating.Field);
            if (now != then)
            {
                notes.Add($"{rating.Label} {then + LowestRating} → {now + LowestRating}");
            }
        }

        long style = table.GetInt(row, "playerstyle");
        long oldStyle = oldTable.GetInt(oldRow, "playerstyle");
        if (style != oldStyle)
        {
            IReadOnlyList<RosterChoice> styles = goalie ? GoalieStyles : SkaterStyles;
            string Show(long value) => styles.FirstOrDefault(s => s.Value == value)?.Label ?? value.ToString(CultureInfo.InvariantCulture);
            notes.Add($"Style {Show(oldStyle)} → {Show(style)}");
        }
    }

    private static void Note(Dictionary<long, List<string>> notes, long player, string note)
    {
        if (!notes.TryGetValue(player, out List<string>? list))
        {
            notes[player] = list = [];
        }

        list.Add(note);
    }

    private static string LetterName(long captain) => captain switch { Captain => "C", Alternate => "A", _ => "None" };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string NameOfBio(int bio) => $"{_bios.GetText(bio, "firstname")} {_bios.GetText(bio, "lastname")}".Trim();

    private string NameOfKey(long key)
    {
        if (_zeroMeansEmpty && key == 0)
        {
            return "nobody";
        }

        return _players.TryGetValue(key, out long id) && _bioRows.TryGetValue(id, out int bio) ? NameOfBio(bio) : "an unknown player";
    }

    private string? ClearCaptain(int team)
    {
        for (int row = 0; row < _slots.RowCount; row++)
        {
            if (_slots.GetInt(row, "team") == team && _slots.GetInt(row, "captain") == Captain && !_slots.TrySetInt(row, "captain", NoCaptain, out string? error))
            {
                return error;
            }
        }

        return null;
    }

    private static readonly string[] BioTextFields = ["firstname", "lastname", "birthplace"];

    private static readonly string[] BioNumberFields = ["position", "handedness", "height", "weight", "year", "month", "day", "jersey"];

    private sealed record TdbTableViewPair(TdbTableView Table, TdbTableView Old, Dictionary<long, int> Rows, Dictionary<long, int> OldRows);
}
