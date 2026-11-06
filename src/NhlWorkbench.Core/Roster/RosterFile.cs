using System.Text.RegularExpressions;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Roster;

/// <summary>A rating the Roster page shows. Stored as 0..63, shown as 36..99.</summary>
public sealed record RosterRating(string Field, string Label, string Group);

/// <summary>One choice for a number field, such as a playing style.</summary>
public sealed record RosterChoice(int Value, string Label);

/// <summary>A player as read at one moment. Change them through <see cref="RosterFile"/>, then read them again.</summary>
public sealed class RosterPlayer
{
    /// <summary>Row in the roster table, or null for a free agent.</summary>
    public int? SlotRow { get; init; }

    /// <summary>Row in the free agent table, or null for a player on a team.</summary>
    public int? FreeAgentRow { get; init; }

    public required int BioRow { get; init; }

    public required int RatingsRow { get; init; }

    public required long PlayerId { get; init; }

    /// <summary>The team's database index, or <see cref="RosterFile.FreeAgents"/>.</summary>
    public required int Team { get; init; }

    public required string FirstName { get; init; }

    public required string LastName { get; init; }

    public required string Birthplace { get; init; }

    public required int Position { get; init; }

    public required int Jersey { get; init; }

    public required int Handedness { get; init; }

    public required int HeightInches { get; init; }

    public required int WeightPounds { get; init; }

    public required int BirthYear { get; init; }

    public required int BirthMonth { get; init; }

    public required int BirthDay { get; init; }

    public required int Captain { get; init; }

    public required int Style { get; init; }

    public required int Overall { get; init; }

    public required string Line { get; init; }

    public required int LineOrder { get; init; }

    public bool IsGoalie => Position == RosterFile.GoaliePosition;

    public bool IsFreeAgent => SlotRow is null;

    public string FullName => $"{FirstName} {LastName}".Trim();

    public string PositionName => RosterFile.PositionNames[Math.Clamp(Position, 0, RosterFile.PositionNames.Count - 1)];

    public string StyleName =>
        (IsGoalie ? RosterFile.GoalieStyles : RosterFile.SkaterStyles).FirstOrDefault(s => s.Value == Style)?.Label ?? string.Empty;
}

/// <summary>
/// A roster: a PS3 roster save (<c>SYS-DATA</c>), which can be edited, or the game's built-in <c>db/nhlng.db</c>, which
/// can only be read because it can't be turned into a save.
/// <para>
/// Teams and players join like this: a roster row names a team and a player key, the key table turns that into a player
/// id, and the id finds the bio and the ratings. One player can be on several teams (a club and a national team). A
/// roster row's own number and captaincy belong to that team, while name and ratings are shared by all of them.
/// </para>
/// <para>
/// Roster rows stay in their team's run of keys, so nothing here adds rows or changes a row's team. A trade swaps who is
/// in two rows. This is how NHL Modding Studio edits rosters, and its research supplies the joins and value offsets.
/// </para>
/// </summary>
public sealed partial class RosterFile
{
    /// <summary>Stands in for a team index to mean the free agent list.</summary>
    public const int FreeAgents = -1;

    public const int GoaliePosition = 4;
    public const int LowestRating = 36;
    public const int HighestRating = 99;
    public const int NoCaptain = 0;
    public const int Captain = 1;
    public const int Alternate = 2;

    public static readonly IReadOnlyList<string> PositionNames = ["C", "LW", "RW", "D", "G"];

    public static readonly IReadOnlyList<RosterChoice> SkaterStyles =
    [
        new(6, "Playmaker"), new(7, "Sniper"), new(8, "Power forward"), new(9, "Two-way forward"), new(5, "Grinder"), new(10, "Enforcer forward"),
        new(1, "Defensive D"), new(2, "Offensive D"), new(4, "Two-way D"), new(3, "Enforcer D"),
    ];

    public static readonly IReadOnlyList<RosterChoice> GoalieStyles = [new(0, "Standup"), new(1, "Hybrid"), new(2, "Butterfly")];

    public static readonly IReadOnlyList<RosterRating> SkaterRatings =
    [
        new("c_overall", "Overall", "Overall"), new("c_potential", "Potential", "Overall"),
        new("c_speed", "Speed", "Skating"), new("c_acceleration", "Acceleration", "Skating"), new("c_agility", "Agility", "Skating"), new("c_balance", "Balance", "Skating"), new("c_endurance", "Endurance", "Skating"),
        new("c_wristshotpower", "Wrist shot power", "Shooting"), new("c_wristaccuracy", "Wrist shot accuracy", "Shooting"), new("c_slapshotpower", "Slap shot power", "Shooting"), new("c_slapaccuracy", "Slap shot accuracy", "Shooting"),
        new("c_passing", "Passing", "Puck skills"), new("c_puckcontrol", "Puck control", "Puck skills"), new("c_deking", "Deking", "Puck skills"), new("c_handeye", "Hand-eye", "Puck skills"), new("c_faceoffs", "Faceoffs", "Puck skills"),
        new("c_offawareness", "Offensive awareness", "Awareness"), new("c_defawareness", "Defensive awareness", "Awareness"), new("c_poise", "Poise", "Awareness"), new("c_discipline", "Discipline", "Awareness"),
        new("c_shotblocking", "Shot blocking", "Defense"), new("c_stickchecking", "Stick checking", "Defense"), new("c_bodychecking", "Body checking", "Defense"),
        new("c_strength", "Strength", "Physical"), new("c_durability", "Durability", "Physical"), new("c_aggression", "Aggression", "Physical"), new("c_fightingskill", "Fighting skill", "Physical"),
    ];

    public static readonly IReadOnlyList<RosterRating> GoalieRatings =
    [
        new("c_overall", "Overall", "Overall"), new("c_potential", "Potential", "Overall"),
        new("c_gsh", "Glove high", "Saves"), new("c_gsl", "Glove low", "Saves"), new("c_ssh", "Stick high", "Saves"), new("c_ssl", "Stick low", "Saves"), new("c_5hole", "Five hole", "Saves"),
        new("c_positioning", "Positioning", "Reading the play"), new("c_reboundcontrol", "Rebound control", "Reading the play"), new("c_shotrecover", "Shot recovery", "Reading the play"), new("c_breakaway", "Breakaways", "Reading the play"), new("c_vision", "Vision", "Reading the play"), new("c_consistency", "Consistency", "Reading the play"), new("c_poise", "Poise", "Reading the play"),
        new("c_pokecheck", "Poke check", "Puck handling"), new("c_passing", "Passing", "Puck handling"), new("c_puckplayfrequency", "Plays the puck", "Puck handling"),
        new("c_speed", "Speed", "Physical"), new("c_agility", "Agility", "Physical"), new("c_endurance", "Endurance", "Physical"), new("c_durability", "Durability", "Physical"), new("c_aggression", "Aggression", "Physical"),
    ];

    private const string SlotsId = "eGlu";
    private const string KeysId = "ZBac";
    private const string BiosId = "ubPc";
    private const string SkatersId = "dSvy";
    private const string GoaliesId = "mHuy";
    private const string FreeAgentsTableName = "exhibitionfreeagents";

    private const int HeightOffset = 54;
    private const int WeightOffset = 120;
    private const int YearOffset = 1900;

    // Roster rows name the even-strength units in one-bit fields such as l1c and l2ld. The other flags are special teams.
    private static readonly Regex LineField = new("^l([1-4])(lw|c|rw|ld|rd)$", RegexOptions.Compiled);

    private readonly TdbDatabase _database;
    private readonly TdbSaveContainer? _container;
    private readonly TdbTableView _slots;
    private readonly TdbTableView _bios;
    private readonly TdbTableView _skaters;
    private readonly TdbTableView _goalies;
    private readonly TdbTableView? _freeAgents;
    private readonly Dictionary<long, long> _players = [];
    private readonly Dictionary<long, int> _bioRows = [];
    private readonly Dictionary<long, int> _skaterRows = [];
    private readonly Dictionary<long, int> _goalieRows = [];
    private readonly bool _zeroMeansEmpty;
    private readonly List<(string Name, int Line, string Spot)> _lineFields = [];

    private RosterFile(TdbDatabase database, TdbSaveContainer? container, TdbTableView slots, TdbTableView keys, TdbTableView bios, TdbTableView skaters, TdbTableView goalies)
    {
        _database = database;
        _container = container;
        _slots = slots;
        _bios = bios;
        _skaters = skaters;
        _goalies = goalies;
        TdbTableMeta? freeAgentMeta = database.Meta.Tables.FirstOrDefault(t => t.Name == FreeAgentsTableName);
        _freeAgents = freeAgentMeta is null ? null : database.GetTable(freeAgentMeta.Id);

        // Where a key appears twice (the mod's save has one such key), the first row is the one used.
        var keysPerPlayer = new Dictionary<long, int>();
        for (int row = 0; row < keys.RowCount; row++)
        {
            long player = keys.GetInt(row, "playerid");
            _players.TryAdd(keys.GetInt(row, "index"), player);
            keysPerPlayer[player] = keysPerPlayer.GetValueOrDefault(player) + 1;
        }

        Fill(_bioRows, bios);
        Fill(_skaterRows, skaters);
        Fill(_goalieRows, goalies);

        // A roster row with key 0 is an empty spot, unless key 0 is the only key some player has.
        _zeroMeansEmpty = !_players.TryGetValue(0, out long zeroPlayer) || keysPerPlayer.GetValueOrDefault(zeroPlayer) > 1;

        foreach (TdbFieldMeta field in slots.Meta.Fields)
        {
            Match match = LineField.Match(field.Name);
            if (match.Success && slots.HasField(field.Name))
            {
                _lineFields.Add((field.Name, int.Parse(match.Groups[1].Value), match.Groups[2].Value.ToUpperInvariant()));
            }
        }
    }

    /// <summary>True for a save, which can be edited. The built-in database is read-only.</summary>
    public bool IsEditable => _container is not null;

    /// <summary>Warnings from opening the file, for showing beside it.</summary>
    public IReadOnlyList<string> ChecksumProblems => _database.ChecksumProblems;

    public static Result<RosterFile> OpenSave(byte[] sysData)
    {
        Result<TdbSaveContainer> save = TdbSaveContainer.Parse(sysData);
        if (!save.IsSuccess)
        {
            return Result.Failure<RosterFile>(save.Error!);
        }

        return Open(save.Value!.InnerDatabase, save.Value);
    }

    public static Result<RosterFile> OpenDatabase(byte[] database) => Open(database, null);

    private static Result<RosterFile> Open(byte[] data, TdbSaveContainer? container)
    {
        try
        {
            Result<TdbDatabase> opened = TdbDatabase.Open(data);
            if (!opened.IsSuccess)
            {
                return Result.Failure<RosterFile>(opened.Error!);
            }

            TdbDatabase db = opened.Value!;
            if (db.SchemaProblems.Count > 0)
            {
                return Result.Failure<RosterFile>($"This roster isn't laid out the way this game's rosters are: {db.SchemaProblems[0]}");
            }

            string[] ids = [SlotsId, KeysId, BiosId, SkatersId, GoaliesId];
            TdbTableView?[] tables = ids.Select(db.GetTable).ToArray();
            int missing = Array.IndexOf(tables, null);
            if (missing >= 0)
            {
                return Result.Failure<RosterFile>($"This file has no '{ids[missing]}' table, so it isn't a roster.");
            }

            return Result.Success(new RosterFile(db, container, tables[0]!, tables[1]!, tables[2]!, tables[3]!, tables[4]!));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
        {
            return Result.Failure<RosterFile>($"Couldn't read this roster: {ex.Message}", ex);
        }
    }

    /// <summary>Every roster spot with a player in it, on one team or on <see cref="FreeAgents"/>.</summary>
    public IReadOnlyList<RosterPlayer> PlayersOn(int team)
    {
        var players = new List<RosterPlayer>();
        if (team == FreeAgents)
        {
            for (int row = 0; row < (_freeAgents?.RowCount ?? 0); row++)
            {
                if (ReadPlayer(_freeAgents!.GetInt(row, "playerindex"), null, row) is { } player)
                {
                    players.Add(player);
                }
            }

            return players.OrderByDescending(p => p.Overall).ThenBy(p => p.LastName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        for (int row = 0; row < _slots.RowCount; row++)
        {
            if (_slots.GetInt(row, "team") == team && !IsOpen(row) && ReadPlayer(_slots.GetInt(row, "playerindex"), row, null) is { } player)
            {
                players.Add(player);
            }
        }

        return players.OrderBy(p => p.LineOrder).ThenByDescending(p => p.Overall).ToList();
    }

    /// <summary>Every team with a row on this roster. Saves carry fewer teams than the disc.</summary>
    public HashSet<int> TeamsOnRoster() =>
        Enumerable.Range(0, _slots.RowCount).Select(row => (int)_slots.GetInt(row, "team")).ToHashSet();

    /// <summary>Players anywhere whose name contains the text, teams first and then free agents.</summary>
    public IReadOnlyList<RosterPlayer> Search(string text, int limit)
    {
        var found = new List<RosterPlayer>();
        for (int row = 0; row < _slots.RowCount && found.Count < limit; row++)
        {
            if (!IsOpen(row) && ReadPlayer(_slots.GetInt(row, "playerindex"), row, null) is { } player && player.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(player);
            }
        }

        for (int row = 0; row < (_freeAgents?.RowCount ?? 0) && found.Count < limit; row++)
        {
            if (ReadPlayer(_freeAgents!.GetInt(row, "playerindex"), null, row) is { } player && player.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(player);
            }
        }

        return found.OrderByDescending(p => p.Overall).ToList();
    }

    /// <summary>Spots on this team's roster with nobody in them.</summary>
    public int OpenSpotsOn(int team) =>
        Enumerable.Range(0, _slots.RowCount).Count(row => _slots.GetInt(row, "team") == team && IsOpen(row));

    /// <summary>Reads a player again after a change. Null if they've gone from this spot.</summary>
    public RosterPlayer? Reread(RosterPlayer player)
    {
        if (player.SlotRow is { } slot)
        {
            return ReadPlayer(_slots.GetInt(slot, "playerindex"), slot, null);
        }

        return player.FreeAgentRow is { } row ? ReadPlayer(_freeAgents!.GetInt(row, "playerindex"), null, row) : null;
    }

    public int RatingOf(RosterPlayer player, string field) =>
        Ratings(player).HasField(field) ? (int)Ratings(player).GetInt(player.RatingsRow, field) + LowestRating : LowestRating;

    public Result<bool> SetName(RosterPlayer player, string firstName, string lastName)
    {
        firstName = firstName.Trim();
        lastName = lastName.Trim();
        if (lastName.Length == 0)
        {
            return Result.Failure<bool>("A player needs a last name.");
        }

        return Apply(() => _bios.TrySetText(player.BioRow, "firstname", firstName, out string? a) ? _bios.TrySetText(player.BioRow, "lastname", lastName, out a) ? null : a : a);
    }

    public Result<bool> SetBirthplace(RosterPlayer player, string place) =>
        Apply(() => _bios.TrySetText(player.BioRow, "birthplace", place.Trim(), out string? error) ? null : error);

    /// <summary>A number belongs to the team's roster row. The bio's copy follows only if it matched.</summary>
    public Result<bool> SetJersey(RosterPlayer player, int jersey)
    {
        if (jersey is < 0 or > 99)
        {
            return Result.Failure<bool>("A number has to be from 0 to 99.");
        }

        if (player.SlotRow is { } slot && Worn(player.Team, slot).FirstOrDefault(w => w.Jersey == jersey) is { Name: not null } other)
        {
            return Result.Failure<bool>($"{other.Name} already wears {jersey} on this team.");
        }

        return WriteJersey(player, jersey);
    }

    /// <summary>Writes a number without looking at who else wears it, for callers that check a whole team afterwards.</summary>
    private Result<bool> WriteJersey(RosterPlayer player, int jersey)
    {
        return Apply(() =>
        {
            if (player.SlotRow is { } row && !_slots.TrySetInt(row, "jersey", jersey, out string? slotError))
            {
                return slotError;
            }

            bool bioFollows = player.IsFreeAgent || _bios.GetInt(player.BioRow, "jersey") == player.Jersey;
            return !bioFollows || _bios.TrySetInt(player.BioRow, "jersey", jersey, out string? bioError) ? null : bioError;
        });
    }

    /// <summary>A team has one captain, so naming a new one takes it from the old one. Alternates aren't limited.</summary>
    public Result<bool> SetCaptain(RosterPlayer player, int captaincy)
    {
        if (player.SlotRow is not { } slot)
        {
            return Result.Failure<bool>("A free agent can't wear a letter.");
        }

        if (captaincy is not (NoCaptain or Captain or Alternate))
        {
            return Result.Failure<bool>("That isn't a captaincy.");
        }

        return Apply(() =>
        {
            if (captaincy == Captain)
            {
                for (int row = 0; row < _slots.RowCount; row++)
                {
                    if (row != slot && _slots.GetInt(row, "team") == player.Team && _slots.GetInt(row, "captain") == Captain && !_slots.TrySetInt(row, "captain", NoCaptain, out string? clearError))
                    {
                        return clearError;
                    }
                }
            }

            return _slots.TrySetInt(slot, "captain", captaincy, out string? error) ? null : error;
        });
    }

    /// <summary>Skaters can change between skating positions. Goalies keep different ratings, so they can't change over.</summary>
    public Result<bool> SetPosition(RosterPlayer player, int position)
    {
        if (position is < 0 or >= GoaliePosition || player.IsGoalie)
        {
            return Result.Failure<bool>("Goalies and skaters have different ratings, so a player can't change between them.");
        }

        return Apply(() => _bios.TrySetInt(player.BioRow, "position", position, out string? error) ? null : error);
    }

    public Result<bool> SetHandedness(RosterPlayer player, int handedness) =>
        Apply(() => _bios.TrySetInt(player.BioRow, "handedness", handedness, out string? error) ? null : error);

    public Result<bool> SetHeight(RosterPlayer player, int inches) =>
        inches is < HeightOffset or > HeightOffset + 31
            ? Result.Failure<bool>("A height has to be from 4'6\" to 7'1\".")
            : Apply(() => _bios.TrySetInt(player.BioRow, "height", inches - HeightOffset, out string? error) ? null : error);

    public Result<bool> SetWeight(RosterPlayer player, int pounds) =>
        pounds is < WeightOffset or > WeightOffset + 255
            ? Result.Failure<bool>("A weight has to be from 120 to 375 pounds.")
            : Apply(() => _bios.TrySetInt(player.BioRow, "weight", pounds - WeightOffset, out string? error) ? null : error);

    public Result<bool> SetBirthDate(RosterPlayer player, int year, int month, int day)
    {
        if (year is < YearOffset or > YearOffset + 255 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return Result.Failure<bool>("That isn't a date the game can hold.");
        }

        return Apply(() =>
            !_bios.TrySetInt(player.BioRow, "year", year - YearOffset, out string? error) ? error
            : !_bios.TrySetInt(player.BioRow, "month", month - 1, out error) ? error
            : !_bios.TrySetInt(player.BioRow, "day", day - 1, out error) ? error
            : null);
    }

    /// <summary>The style is kept with the ratings and again on each team's roster row.</summary>
    public Result<bool> SetStyle(RosterPlayer player, int style)
    {
        if ((player.IsGoalie ? GoalieStyles : SkaterStyles).All(s => s.Value != style))
        {
            return Result.Failure<bool>("That style isn't one for this kind of player.");
        }

        return Apply(() =>
        {
            if (!Ratings(player).TrySetInt(player.RatingsRow, "playerstyle", style, out string? error))
            {
                return error;
            }

            for (int row = 0; row < _slots.RowCount; row++)
            {
                if (!IsOpen(row) && _players.GetValueOrDefault(_slots.GetInt(row, "playerindex"), -1) == player.PlayerId && !_slots.TrySetInt(row, "playerstyle", style, out error))
                {
                    return error;
                }
            }

            return null;
        });
    }

    public Result<bool> SetRating(RosterPlayer player, string field, int rating)
    {
        if (rating is < LowestRating or > HighestRating)
        {
            return Result.Failure<bool>($"A rating has to be from {LowestRating} to {HighestRating}.");
        }

        return Apply(() => Ratings(player).TrySetInt(player.RatingsRow, field, rating - LowestRating, out string? error) ? null : error);
    }

    /// <summary>
    /// Swaps two players: each takes the other's spot on the roster. The lines stay with the spot, so nobody's line is
    /// left empty and the arriving player plays the departing player's minutes. Letters don't travel, and a number already
    /// worn on the new team changes to the lowest free one. Returns what happened, for telling the user.
    /// </summary>
    public Result<string> Trade(RosterPlayer first, RosterPlayer second)
    {
        if (first.IsFreeAgent && second.IsFreeAgent)
        {
            return Result.Failure<string>("Pick a player on a team for one side of the swap.");
        }

        if (first.Team == second.Team)
        {
            return Result.Failure<string>("Those two are on the same team.");
        }

        if (first.PlayerId == second.PlayerId)
        {
            return Result.Failure<string>("That's the same player on two teams.");
        }

        if (first.IsGoalie != second.IsGoalie)
        {
            return Result.Failure<string>("A goalie can only be traded for a goalie, because the other spot's lines are for skaters.");
        }

        if (OnTeam(first.PlayerId, second.Team) || OnTeam(second.PlayerId, first.Team))
        {
            return Result.Failure<string>("One of them is already on the other team.");
        }

        (RosterPlayer team, RosterPlayer other) = first.IsFreeAgent ? (second, first) : (first, second);
        string? error = other.IsFreeAgent ? SwapWithFreeAgent(team, other) : SwapSlots(team, other);
        if (error is not null)
        {
            return Result.Failure<string>(error);
        }

        var notes = new List<string>();
        RosterPlayer? teamNow = Reread(team);
        RosterPlayer? otherNow = Reread(other);
        foreach ((RosterPlayer before, RosterPlayer? after) in new[] { (team, otherNow), (other, teamNow) })
        {
            if (after is not null && !after.IsFreeAgent && after.Jersey != before.Jersey)
            {
                notes.Add($"{after.FullName} now wears {after.Jersey}.");
            }
        }

        string lines = !team.IsGoalie && team.Position != other.Position
            ? $" They play different positions and swapped lines, so check {team.PositionName} and {other.PositionName} in the game's lines."
            : string.Empty;
        return Result.Success($"Swapped {team.FullName} and {other.FullName}.{lines} {string.Join(" ", notes)}".TrimEnd());
    }

    /// <summary>The save file as it now stands. Fails if this is the built-in database, or the save would have to grow.</summary>
    public Result<byte[]> BuildSave() =>
        _container is null ? Result.Failure<byte[]>("The game's built-in roster can't be saved. Start from a roster save.") : _container.Build(_database.ToBytes());

    /// <summary>The database inside, for building the save on another thread after copying it here.</summary>
    public byte[] DatabaseBytes() => _database.ToBytes();

    /// <summary>Builds a save from <see cref="DatabaseBytes"/>. It touches nothing that edits change, so any thread can call it.</summary>
    public Result<byte[]> BuildSave(byte[] databaseBytes) =>
        _container is null ? Result.Failure<byte[]>("The game's built-in roster can't be saved. Start from a roster save.") : _container.Build(databaseBytes);

    private static void Fill(Dictionary<long, int> rows, TdbTableView table)
    {
        for (int row = 0; row < table.RowCount; row++)
        {
            rows.TryAdd(table.GetInt(row, "game_id"), row);
        }
    }

    private TdbTableView Ratings(RosterPlayer player) => player.IsGoalie ? _goalies : _skaters;

    private bool IsOpen(int slot) => _zeroMeansEmpty && _slots.GetInt(slot, "playerindex") == 0;

    private bool OnTeam(long playerId, int team) =>
        team != FreeAgents && Enumerable.Range(0, _slots.RowCount).Any(row =>
            _slots.GetInt(row, "team") == team && !IsOpen(row) && _players.GetValueOrDefault(_slots.GetInt(row, "playerindex"), -1) == playerId);

    private IEnumerable<(int Jersey, string? Name)> Worn(int team, int exceptSlot)
    {
        for (int row = 0; row < _slots.RowCount; row++)
        {
            if (row != exceptSlot && _slots.GetInt(row, "team") == team && !IsOpen(row))
            {
                long key = _slots.GetInt(row, "playerindex");
                string name = _players.TryGetValue(key, out long player) && _bioRows.TryGetValue(player, out int bio)
                    ? $"{_bios.GetText(bio, "firstname")} {_bios.GetText(bio, "lastname")}".Trim()
                    : "Another player";
                yield return ((int)_slots.GetInt(row, "jersey"), name);
            }
        }
    }

    private RosterPlayer? ReadPlayer(long key, int? slot, int? freeAgentRow)
    {
        if (!_players.TryGetValue(key, out long id) || !_bioRows.TryGetValue(id, out int bio))
        {
            return null;
        }

        int position = (int)_bios.GetInt(bio, "position");
        bool goalie = position == GoaliePosition;
        Dictionary<long, int> rows = goalie ? _goalieRows : _skaterRows;
        if (!rows.TryGetValue(id, out int ratings))
        {
            return null;
        }

        TdbTableView table = goalie ? _goalies : _skaters;
        (string line, int order) = slot is { } s ? DescribeLine(s, position) : ("Free agent", 900);
        return new RosterPlayer
        {
            SlotRow = slot,
            FreeAgentRow = freeAgentRow,
            BioRow = bio,
            RatingsRow = ratings,
            PlayerId = id,
            Team = slot is { } team ? (int)_slots.GetInt(team, "team") : FreeAgents,
            FirstName = _bios.GetText(bio, "firstname"),
            LastName = _bios.GetText(bio, "lastname"),
            Birthplace = _bios.GetText(bio, "birthplace"),
            Position = position,
            Jersey = slot is { } jersey ? (int)_slots.GetInt(jersey, "jersey") : (int)_bios.GetInt(bio, "jersey"),
            Handedness = (int)_bios.GetInt(bio, "handedness"),
            HeightInches = (int)_bios.GetInt(bio, "height") + HeightOffset,
            WeightPounds = (int)_bios.GetInt(bio, "weight") + WeightOffset,
            BirthYear = (int)_bios.GetInt(bio, "year") + YearOffset,
            BirthMonth = (int)_bios.GetInt(bio, "month") + 1,
            BirthDay = (int)_bios.GetInt(bio, "day") + 1,
            Captain = slot is { } letter ? (int)_slots.GetInt(letter, "captain") : NoCaptain,
            Style = (int)table.GetInt(ratings, "playerstyle"),
            Overall = (int)table.GetInt(ratings, "c_overall") + LowestRating,
            Line = line,
            LineOrder = order,
        };
    }

    private (string Label, int Order) DescribeLine(int slot, int position)
    {
        if (position == GoaliePosition)
        {
            if (_slots.HasField("g1") && _slots.GetInt(slot, "g1") != 0)
            {
                return ("Starter", 1);
            }

            return _slots.HasField("g2") && _slots.GetInt(slot, "g2") != 0 ? ("Backup", 2) : ("Not dressed", 500);
        }

        foreach ((string name, int line, string spot) in _lineFields)
        {
            if (_slots.GetInt(slot, name) != 0)
            {
                int across = spot switch { "LW" or "LD" => 0, "C" => 1, _ => 2 };
                return ($"{(spot is "LD" or "RD" ? "Pair" : "Line")} {line} {spot}", (line * 10) + across);
            }
        }

        return ("Not dressed", 500);
    }

    private string? SwapSlots(RosterPlayer a, RosterPlayer b)
    {
        int slotA = a.SlotRow!.Value;
        int slotB = b.SlotRow!.Value;
        long keyA = _slots.GetInt(slotA, "playerindex");
        long keyB = _slots.GetInt(slotB, "playerindex");
        long styleA = _slots.GetInt(slotA, "playerstyle");
        long styleB = _slots.GetInt(slotB, "playerstyle");

        return Run(
            () => _slots.TrySetInt(slotA, "playerindex", keyB, out string? e) ? null : e,
            () => _slots.TrySetInt(slotB, "playerindex", keyA, out string? e) ? null : e,
            () => _slots.TrySetInt(slotA, "playerstyle", styleB, out string? e) ? null : e,
            () => _slots.TrySetInt(slotB, "playerstyle", styleA, out string? e) ? null : e,
            () => _slots.TrySetInt(slotA, "captain", NoCaptain, out string? e) ? null : e,
            () => _slots.TrySetInt(slotB, "captain", NoCaptain, out string? e) ? null : e,
            () => Arrive(slotA, b.Jersey),
            () => Arrive(slotB, a.Jersey),
            () => FollowTeam(a, b.Team),
            () => FollowTeam(b, a.Team));
    }

    private string? SwapWithFreeAgent(RosterPlayer onTeam, RosterPlayer freeAgent)
    {
        int slot = onTeam.SlotRow!.Value;
        int row = freeAgent.FreeAgentRow!.Value;
        long keyOut = _slots.GetInt(slot, "playerindex");
        long keyIn = _freeAgents!.GetInt(row, "playerindex");
        long oldBioTeam = _bios.GetInt(freeAgent.BioRow, "team");

        return Run(
            () => _slots.TrySetInt(slot, "playerindex", keyIn, out string? e) ? null : e,
            () => _freeAgents.TrySetInt(row, "playerindex", keyOut, out string? e) ? null : e,
            () => _slots.TrySetInt(slot, "playerstyle", freeAgent.Style, out string? e) ? null : e,
            () => _slots.TrySetInt(slot, "captain", NoCaptain, out string? e) ? null : e,
            () => Arrive(slot, freeAgent.Jersey),
            () => _bios.TrySetInt(freeAgent.BioRow, "team", onTeam.Team + 1, out string? e) ? null : e,
            () => _bios.GetInt(onTeam.BioRow, "team") == onTeam.Team + 1 && !_bios.TrySetInt(onTeam.BioRow, "team", oldBioTeam, out string? e) ? e : null);
    }

    /// <summary>Puts the player's number on their new roster row, or the lowest free one if it's taken.</summary>
    private string? Arrive(int slot, int jersey)
    {
        var worn = Worn((int)_slots.GetInt(slot, "team"), slot).Select(w => w.Jersey).ToHashSet();
        int number = worn.Contains(jersey) ? Enumerable.Range(1, 98).First(n => !worn.Contains(n)) : jersey;
        return _slots.TrySetInt(slot, "jersey", number, out string? error) ? null : error;
    }

    /// <summary>The bio's team is one above the roster's team index. It's left alone when it pointed somewhere else.</summary>
    private string? FollowTeam(RosterPlayer player, int newTeam) =>
        _bios.GetInt(player.BioRow, "team") != player.Team + 1 || _bios.TrySetInt(player.BioRow, "team", newTeam + 1, out string? error) ? null : error;

    private Result<bool> Apply(Func<string?> change)
    {
        if (!IsEditable)
        {
            return Result.Failure<bool>("This roster is read-only.");
        }

        string? error = change();
        return error is null ? Result.Success(true) : Result.Failure<bool>(error);
    }

    private string? Run(params Func<string?>[] steps)
    {
        if (!IsEditable)
        {
            return "This roster is read-only.";
        }

        foreach (Func<string?> step in steps)
        {
            if (step() is { } error)
            {
                return error;
            }
        }

        return null;
    }
}
