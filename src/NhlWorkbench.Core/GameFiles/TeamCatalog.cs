using System.Text.RegularExpressions;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.GameFiles;

public enum TeamSection
{
    Logos,
    IceAndPuck,
    Arena,
    Uniforms,
    GoalieMasks,
}

/// <summary>One file in a team's bundle. <see cref="Group"/> is the heading it sits under.</summary>
public sealed record TeamArt(string Path, TeamSection Section, string Group, string Label);

/// <summary>A uniform: the pieces that share a style, team and variant. A spare kit has files but no database row, so it isn't on offer.</summary>
public sealed record TeamKit(
    int Style,
    int Variant,
    string Title,
    string Note,
    bool IsListed,
    IReadOnlyList<TeamArt> Pieces);

public readonly record struct TeamColor(byte R, byte G, byte B);

/// <summary>A team and everything in the game that's drawn for it.</summary>
public sealed record GameTeam(
    int Index,
    int ArtId,
    string Name,
    string Abbreviation,
    int LeagueCode,
    string League,
    TeamColor Primary,
    TeamColor Secondary,
    string ArenaName,
    IReadOnlyList<TeamKit> Kits,
    IReadOnlyList<TeamArt> Art)
{
    /// <summary>False for art the game ships under a number no team in its database uses.</summary>
    public bool HasDatabaseRow => Index >= 0;

    public string? CrestPath => Art.FirstOrDefault(a => a.Label == TeamCatalog.CrestLabel)?.Path;

    public IEnumerable<TeamArt> AllArt => Art.Concat(Kits.SelectMany(k => k.Pieces));
}

/// <summary>Who a file belongs to.</summary>
public sealed record TeamArtOwner(GameTeam Team, TeamArt Art);

/// <summary>
/// Groups the game's art by team. The game's database lists the teams, and each team's art number is what its files are
/// named after: exhibitionteams.artid, stockteamjerseys for kits, exhibitionarena.arenaartid for its building, and
/// goaliemask slots for mask paints. Gear such as gloves and sticks goes by brand, not team, so it isn't here.
/// Mapping credit: the research in NHL Modding Studio's docs; every link is re-measured by this project's tests.
/// </summary>
public sealed class TeamCatalog
{
    public const string CrestLabel = "Crest";
    public const string UnlistedLeague = "No team in the database";

    private const int LeagueCustomTeams = 13;
    private const int LeagueWinterClassic = 14;
    private const int MaskSlots = 7;
    private const string ArenaTableId = "StEO";

    private static readonly IReadOnlyDictionary<int, string> LeagueNames = new Dictionary<int, string>
    {
        [0] = "NHL", [1] = "AHL", [2] = "SHL", [3] = "Liiga", [4] = "DEL", [5] = "Extraliga", [6] = "Swiss National League",
        [7] = "GET-ligaen", [8] = "National teams", [9] = "OHL", [10] = "QMJHL", [11] = "WHL", [12] = "Top Prospects",
        [LeagueCustomTeams] = "Custom teams", [LeagueWinterClassic] = "Winter Classic", [15] = "EASHL",
    };

    private static readonly int[] LeagueOrder = [0, 14, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15];

    private static readonly Regex JerseyFile = new(@"^rendering/jersey/texlib_(\d+)_(\d+)_(\d+)\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex NameplateFile = new(@"^rendering/jersey/name_(\d+)_(\d+)_(\d+)_cm\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex PantFile = new(@"^rendering/pant/texlib_(\d+)_(\d+)_(\d+)\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex SockFile = new(@"^rendering/sock/sock_(\d+)_(\d+)_(\d+)_cm\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex MenuJerseyFile = new(@"^fe/ion/artassets/jerseys/jersey_(\d+)_(\d+)_(\d+)\.big$", RegexOptions.Compiled);
    private static readonly Regex MaskFile = new(@"^rendering/goaliehelmet/goaliehelmet_(\d+)_(\d+)_cm\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex StadiumFile = new(@"^rendering/stadium/stadium_(\d+)_container_\d+\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex JumbotronFile = new(@"^rendering/jumbotron/texlib_(\d+)\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex LogoSetFile = new(@"^rendering/logo/texlib_(\d+)_(\d+)_(\d+)\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex BannerFile = new(@"^rendering/banner/banner_(\d+)(?:_\d+)?(?:_[a-z]+)?\.rpsgl$", RegexOptions.Compiled);

    private static readonly (Regex Pattern, TeamSection Section, string Group, string Label)[] SingleFiles =
    [
        (new(@"^fe/ion/artassets/teamlogos/t(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", CrestLabel),
        (new(@"^fe/ion/artassets/teamlogossmall/s(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", "Small crest"),
        (new(@"^fe/ion/artassets/teamlogoswide/w(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", "Wide logo"),
        (new(@"^fe/ion/artassets/teamlogoscalendar/c(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", "Calendar logo"),
        (new(@"^fe/ion/artassets/teamlogosdynasty/d(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", "Dynasty logo"),
        (new(@"^fe/ion/artassets/teamlogosreflection/r(\d+)\.big$", RegexOptions.Compiled), TeamSection.Logos, "Menu logos", "Reflection logo"),
        (new(@"^rendering/icesurface/centerlogo_(\d+)_cm\.rpsgl$", RegexOptions.Compiled), TeamSection.IceAndPuck, "Ice and puck", "Center ice"),
        (new(@"^rendering/puck/puck_(\d+)_cm\.rpsgl$", RegexOptions.Compiled), TeamSection.IceAndPuck, "Ice and puck", "Puck"),
    ];

    private readonly IReadOnlyDictionary<string, IReadOnlyList<TeamArtOwner>> _owners;

    private TeamCatalog(IReadOnlyList<GameTeam> teams)
    {
        Teams = teams;
        var owners = new Dictionary<string, List<TeamArtOwner>>(StringComparer.Ordinal);
        foreach (GameTeam team in teams)
        {
            foreach (TeamArt art in team.AllArt)
            {
                if (!owners.TryGetValue(art.Path, out List<TeamArtOwner>? list))
                {
                    owners[art.Path] = list = [];
                }

                list.Add(new TeamArtOwner(team, art));
            }
        }

        _owners = owners.ToDictionary(p => p.Key, p => (IReadOnlyList<TeamArtOwner>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>Teams in the game's league order, then by name. Art no team claims comes last.</summary>
    public IReadOnlyList<GameTeam> Teams { get; }

    public IReadOnlyList<TeamArtOwner> OwnersOf(string gamePath) => _owners.GetValueOrDefault(gamePath) ?? [];

    /// <summary>The team named after an art number, leaving out any second row that borrows the same art.</summary>
    public GameTeam? TeamWithArt(int artId) =>
        Teams.Where(t => t.ArtId == artId).OrderBy(t => t.LeagueCode == LeagueWinterClassic).FirstOrDefault();

    /// <summary>Every file belonging to teams whose name or abbreviation fits all the words.</summary>
    public IEnumerable<string> PathsForTeams(string query)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return Teams
            .Where(t => t.HasDatabaseRow && words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Abbreviation.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(t => t.AllArt.Select(a => a.Path))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Reads the teams from the game's own database and groups its art under them. Only reads file names, so it's quick.</summary>
    public static Result<TeamCatalog> Build(GameFileIndex index)
    {
        Result<TdbDatabase> database = OpenDatabase(index);
        if (!database.IsSuccess)
        {
            return Result.Failure<TeamCatalog>(database.Error!, database.Exception);
        }

        TdbTableView? teamTable = database.Value!.GetTable(TdbDatabase.TeamsTableId);
        TdbTableView? kitTable = database.Value.GetTable(TdbDatabase.JerseysTableId);
        TdbTableView? arenaTable = database.Value.GetTable(ArenaTableId);
        if (teamTable is null || kitTable is null || arenaTable is null)
        {
            return Result.Failure<TeamCatalog>("The game's database has no team, uniform or arena table.");
        }

        TdbLanguageFile? names = OpenNames(index);
        FileGroups files = FileGroups.From(index);
        var arenas = Enumerable.Range(0, arenaTable.RowCount).ToDictionary(r => arenaTable.GetInt(r, "index"));

        var rows = new List<TeamRow>();
        for (int row = 0; row < teamTable.RowCount; row++)
        {
            int league = (int)teamTable.GetInt(row, "league");
            if (league != LeagueCustomTeams)
            {
                rows.Add(new TeamRow(row, (int)teamTable.GetInt(row, "index"), (int)teamTable.GetInt(row, "artid"), league));
            }
        }

        // Kits on the database's list, per team row. Anything left in a team's folder afterward is a spare.
        var listed = new HashSet<(int, int, int)>();
        var kitRows = new Dictionary<int, List<int>>();
        for (int row = 0; row < kitTable.RowCount; row++)
        {
            int teamId = (int)kitTable.GetInt(row, "teamid");
            if (!kitRows.TryGetValue(teamId, out List<int>? list))
            {
                kitRows[teamId] = list = [];
            }

            list.Add(row);
            listed.Add(((int)kitTable.GetInt(row, "style"), (int)kitTable.GetInt(row, "teamartid"), (int)kitTable.GetInt(row, "variant")));
        }

        var firstRowForArt = new Dictionary<int, int>();
        foreach (TeamRow team in rows.Where(r => r.League != LeagueWinterClassic).Concat(rows.Where(r => r.League == LeagueWinterClassic)))
        {
            firstRowForArt.TryAdd(team.ArtId, team.Index);
        }

        var teams = new List<GameTeam>();
        foreach (TeamRow team in rows)
        {
            int row = team.Row;
            var art = new List<TeamArt>(files.ArtFor(team.ArtId));

            string arenaName = string.Empty;
            if (arenas.TryGetValue(teamTable.GetInt(row, "arenaid"), out int arenaRow))
            {
                arenaName = Clean(arenaTable.GetText(arenaRow, "arenaname"));
                art.AddRange(files.ArenaArt((int)arenaTable.GetInt(arenaRow, "arenaartid")));
            }

            art.AddRange(MasksFor(teamTable, row, files));

            var kits = new List<TeamKit>();
            foreach (int kitRow in (kitRows.GetValueOrDefault(team.Index) ?? []).OrderBy(k => kitTable.GetInt(k, "jerseyindex")))
            {
                kits.Add(ListedKit(kitTable, kitRow, kits.Count + 1, files));
            }

            if (firstRowForArt[team.ArtId] == team.Index)
            {
                kits.AddRange(SpareKits(team.ArtId, listed, files));
            }

            teams.Add(new GameTeam(
                team.Index,
                team.ArtId,
                TeamName(teamTable, row, team.League, names),
                Clean(teamTable.GetText(row, "abbrname")),
                team.League,
                LeagueNames.GetValueOrDefault(team.League) ?? $"League {team.League}",
                ColorOf(teamTable, row, "primarycolor"),
                ColorOf(teamTable, row, "secondarycolor"),
                arenaName,
                kits,
                art));
        }

        // Art that ships under a number no team uses. It's still in the game, so it stays reachable.
        var known = rows.Select(r => r.ArtId).ToHashSet();
        foreach (int artId in files.AllArtIds.Where(id => !known.Contains(id)).Order())
        {
            teams.Add(new GameTeam(-1, artId, $"Art {artId}", string.Empty, int.MaxValue, UnlistedLeague, default, default, string.Empty, SpareKits(artId, listed, files), files.ArtFor(artId)));
        }

        List<GameTeam> ordered = teams
            .OrderBy(t => Array.IndexOf(LeagueOrder, t.LeagueCode) is var at && at >= 0 ? at : int.MaxValue)
            .ThenBy(t => t.HasDatabaseRow ? t.Name : string.Empty, StringComparer.CurrentCulture)
            .ThenBy(t => t.ArtId)
            .ToList();
        return Result.Success(new TeamCatalog(ordered));
    }

    private static Result<TdbDatabase> OpenDatabase(GameFileIndex index)
    {
        GameFile? file = index.Find("db/nhlng.db");
        if (file is null)
        {
            return Result.Failure<TdbDatabase>("The game's database, db/nhlng.db, wasn't found.");
        }

        // What the game will actually load comes first, in case a mod renamed teams. The game's own copy is the fallback.
        string error = "The game's database can't be read.";
        foreach (GameFileCopy copy in new[] { file.Winner, TextureCatalog.OriginalCopy(file) }.OfType<GameFileCopy>().Distinct())
        {
            Result<byte[]> data = index.ReadDecoded(copy);
            Result<TdbDatabase> opened = data.IsSuccess ? TdbDatabase.Open(data.Value!) : Result.Failure<TdbDatabase>(data.Error!);
            if (opened.IsSuccess && opened.Value!.SchemaProblems.Count == 0)
            {
                return opened;
            }

            error = opened.Error ?? error;
        }

        return Result.Failure<TdbDatabase>(error);
    }

    private static TdbLanguageFile? OpenNames(GameFileIndex index)
    {
        GameFile? file = index.Find("fe/loc/nhl_eng_us.db");
        Result<byte[]>? data = file is null ? null : index.ReadDecoded(file.Winner);
        return data is { IsSuccess: true } && TdbLanguageFile.Parse(data.Value!) is { IsSuccess: true } parsed ? parsed.Value : null;
    }

    private static string TeamName(TdbTableView table, int row, int league, TdbLanguageFile? names)
    {
        // The game's dictionary holds its display name, and it spells more of them properly than the database does.
        string name = string.Empty;
        string key = table.GetText(row, "artabbr");
        if (key.Length > 0 && names is not null && names.TryGetText("NHLTeamName_" + key, out string text))
        {
            name = Clean(text);
        }

        if (name.Length == 0)
        {
            name = Clean(table.GetText(row, "fullname"));
        }

        return league == LeagueWinterClassic ? name + " (Winter Classic)" : name;
    }

    private static TeamColor ColorOf(TdbTableView table, int row, string field) =>
        new((byte)table.GetInt(row, field + "_r"), (byte)table.GetInt(row, field + "_g"), (byte)table.GetInt(row, field + "_b"));

    private static string Clean(string text) =>
        Regex.Replace(text.Replace("®", string.Empty).Replace("™", string.Empty).Replace("℠", string.Empty), @"\s+", " ").Trim();

    private static TeamKit ListedKit(TdbTableView table, int row, int number, FileGroups files)
    {
        int style = (int)table.GetInt(row, "style");
        int artId = (int)table.GetInt(row, "teamartid");
        int variant = (int)table.GetInt(row, "variant");

        var tags = new List<string>();
        if (table.GetInt(row, "isdefault") != 0)
        {
            tags.Add("Default");
        }

        tags.Add(table.GetInt(row, "islightcolor") != 0 ? "Light" : "Dark");
        if (table.GetInt(row, "hastie") != 0)
        {
            tags.Add("Tie");
        }

        if (table.GetInt(row, "helmetlogo") != 0)
        {
            tags.Add("Helmet logo");
        }

        if (table.GetInt(row, "helmetnumbers") != 0)
        {
            tags.Add("Helmet numbers");
        }

        if (table.GetInt(row, "islockable") != 0)
        {
            tags.Add("Lockable");
        }

        return new TeamKit(style, variant, $"Kit {number}", string.Join(" · ", tags), true, files.KitPieces(style, artId, variant, $"Kit {number}"));
    }

    private static List<TeamKit> SpareKits(int artId, HashSet<(int, int, int)> listed, FileGroups files)
    {
        var kits = new List<TeamKit>();
        foreach ((int style, int variant) in files.KitsFor(artId))
        {
            if (!listed.Contains((style, artId, variant)))
            {
                string title = $"Spare {style}-{variant}";
                kits.Add(new TeamKit(style, variant, title, "Not on the database's kit list, so the game may not offer it", false, files.KitPieces(style, artId, variant, title)));
            }
        }

        return kits;
    }

    private static IEnumerable<TeamArt> MasksFor(TdbTableView table, int row, FileGroups files)
    {
        var seen = new HashSet<int>();
        foreach (string role in new[] { "starter", "backup" })
        {
            for (int slot = 0; slot < MaskSlots; slot++)
            {
                string field = $"goaliemask{slot}_{role}";
                int paint = table.HasField(field) ? (int)table.GetInt(row, field) : 0;
                if (paint == 0 || !seen.Add(paint))
                {
                    continue;
                }

                foreach ((int shell, string path) in files.MasksWithPaint(paint))
                {
                    yield return new TeamArt(path, TeamSection.GoalieMasks, "Goalie masks", $"Paint {paint}, shell {shell}");
                }
            }
        }
    }

    private readonly record struct TeamRow(int Row, int Index, int ArtId, int League);

    /// <summary>The game's file names, sorted into what each art number has.</summary>
    private sealed class FileGroups
    {
        private readonly Dictionary<int, List<TeamArt>> _art = [];
        private readonly Dictionary<int, List<TeamArt>> _arena = [];
        private readonly Dictionary<(int Style, int ArtId, int Variant), List<(int Order, string Path, string Label)>> _kits = [];
        private readonly Dictionary<int, List<(int Shell, string Path)>> _masks = [];

        public IEnumerable<int> AllArtIds => _art.Keys.Concat(_kits.Keys.Select(k => k.ArtId)).Distinct();

        public static FileGroups From(GameFileIndex index)
        {
            var groups = new FileGroups();
            foreach (GameFile file in index.Files)
            {
                if (file.Path.StartsWith("rendering/", StringComparison.Ordinal) || file.Path.StartsWith("fe/ion/artassets/", StringComparison.Ordinal))
                {
                    groups.Add(file.Path);
                }
            }

            return groups;
        }

        public IReadOnlyList<TeamArt> ArtFor(int artId) =>
            _art.TryGetValue(artId, out List<TeamArt>? art)
                ? art.OrderBy(a => a.Section).ThenBy(a => MenuLogoOrder(a.Label)).ThenBy(a => Regex.Replace(a.Label, @"\d+", m => m.Value.PadLeft(6, '0')), StringComparer.Ordinal).ThenBy(a => a.Path, StringComparer.Ordinal).ToList()
                : [];

        public IReadOnlyList<TeamArt> ArenaArt(int arenaArtId) => _arena.TryGetValue(arenaArtId, out List<TeamArt>? art) ? art : [];

        public IEnumerable<(int Style, int Variant)> KitsFor(int artId) =>
            _kits.Keys.Where(k => k.ArtId == artId).OrderBy(k => k.Style).ThenBy(k => k.Variant).Select(k => (k.Style, k.Variant));

        public IReadOnlyList<TeamArt> KitPieces(int style, int artId, int variant, string kitTitle) =>
            _kits.TryGetValue((style, artId, variant), out var pieces)
                ? pieces.OrderBy(p => p.Order).Select(p => new TeamArt(p.Path, TeamSection.Uniforms, kitTitle, p.Label)).ToList()
                : [];

        /// <summary>The big crest first, in the order the file families are listed above.</summary>
        private static int MenuLogoOrder(string label)
        {
            int at = Array.FindIndex(SingleFiles, f => f.Label == label);
            return at >= 0 ? at : SingleFiles.Length;
        }

        public IEnumerable<(int Shell, string Path)> MasksWithPaint(int paint) =>
            _masks.TryGetValue(paint, out var masks) ? masks.OrderBy(m => m.Shell) : [];

        private void Add(string path)
        {
            if (AddKitPiece(path, JerseyFile, 0, "Jersey")
                || AddKitPiece(path, PantFile, 1, "Pants")
                || AddKitPiece(path, SockFile, 2, "Socks")
                || AddKitPiece(path, NameplateFile, 3, "Nameplate")
                || AddKitPiece(path, MenuJerseyFile, 4, "Menu jersey"))
            {
                return;
            }

            foreach ((Regex pattern, TeamSection section, string group, string label) in SingleFiles)
            {
                if (pattern.Match(path) is { Success: true } single)
                {
                    AddArt(_art, Id(single, 1), new TeamArt(path, section, group, label));
                    return;
                }
            }

            if (LogoSetFile.Match(path) is { Success: true } logo)
            {
                AddArt(_art, Id(logo, 1), new TeamArt(path, TeamSection.Logos, "Team logo sets", $"Set {Id(logo, 2)}-{Id(logo, 3)}"));
            }
            else if (BannerFile.Match(path) is { Success: true } banner)
            {
                AddArt(_art, Id(banner, 1), new TeamArt(path, TeamSection.Arena, "Banners", "Banner"));
            }
            else if (StadiumFile.Match(path) is { Success: true } stadium)
            {
                AddArt(_arena, Id(stadium, 1), new TeamArt(path, TeamSection.Arena, "Building", "Stadium"));
            }
            else if (JumbotronFile.Match(path) is { Success: true } jumbotron)
            {
                AddArt(_arena, Id(jumbotron, 1), new TeamArt(path, TeamSection.Arena, "Building", "Jumbotron"));
            }
            else if (MaskFile.Match(path) is { Success: true } mask)
            {
                if (!_masks.TryGetValue(Id(mask, 2), out var list))
                {
                    _masks[Id(mask, 2)] = list = [];
                }

                list.Add((Id(mask, 1), path));
            }
        }

        private bool AddKitPiece(string path, Regex pattern, int order, string label)
        {
            Match match = pattern.Match(path);
            if (!match.Success)
            {
                return false;
            }

            var key = (Id(match, 1), Id(match, 2), Id(match, 3));
            if (!_kits.TryGetValue(key, out var pieces))
            {
                _kits[key] = pieces = [];
            }

            pieces.Add((order, path, label));
            return true;
        }

        private static void AddArt(Dictionary<int, List<TeamArt>> into, int id, TeamArt art)
        {
            if (!into.TryGetValue(id, out List<TeamArt>? list))
            {
                into[id] = list = [];
            }

            list.Add(art);
        }

        private static int Id(Match match, int group) => int.TryParse(match.Groups[group].ValueSpan, out int value) ? value : -1;
    }
}
