using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Roster;

/// <summary>Somewhere a roster can be read from. Only the current mod's own roster is ever written to.</summary>
public sealed record RosterSource(string Title, string Note, string? SavePath, bool IsBuiltIn)
{
    public string? IconPath => SavePath is null ? null : Path.Combine(Path.GetDirectoryName(SavePath)!, "ICON0.PNG");
}

/// <summary>Finds the rosters a game has: what each installed mod carries, the player's own save and the built-in one.</summary>
public static class RosterSources
{
    private const string SaveFolder = "user/gamedata/dev_hdd0/home/00000001/savedata/BLUS315400200";

    /// <summary>The roster the launcher has chosen comes first, since that's what the game is playing.</summary>
    public static IReadOnlyList<RosterSource> Find(string? installRoot, GameFileIndex? index)
    {
        var sources = new List<RosterSource>();
        if (installRoot is not null)
        {
            string? active = ActiveRosterName(installRoot);
            try
            {
                string mods = Path.Combine(installRoot, "mods");
                if (Directory.Exists(mods))
                {
                    foreach (string folder in Directory.EnumerateDirectories(mods).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        string save = Path.Combine(folder, "roster", "SYS-DATA");
                        string name = Path.GetFileName(folder);
                        if (File.Exists(save))
                        {
                            bool isActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
                            sources.Add(new RosterSource(name, isActive ? "The roster the launcher has chosen" : "Carried by this installed mod", save, false));
                        }
                    }
                }

                string own = Path.Combine(installRoot, SaveFolder.Replace('/', Path.DirectorySeparatorChar), "SYS-DATA");
                if (File.Exists(own))
                {
                    sources.Add(new RosterSource("Roster in the game's save", "What the game loaded last", own, false));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The built-in roster below can still be shown.
            }

            sources = sources.OrderByDescending(s => string.Equals(s.Title, active, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (index?.Find("db/nhlng.db") is not null)
        {
            sources.Add(new RosterSource("Built-in roster", "The roster on the disc. It can be looked at, but it isn't a save, so it can't be edited", null, true));
        }

        return sources;
    }

    /// <summary>The mod named in the launcher's settings as the one whose roster to use, if any.</summary>
    public static string? ActiveRosterName(string installRoot)
    {
        string path = Path.Combine(installRoot, "nhl_settings.ini");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            bool inLauncher = false;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inLauncher = line.Equals("[launcher]", StringComparison.OrdinalIgnoreCase);
                }
                else if (inLauncher && line.Split('=', 2) is [var key, var value] && key.Trim().Equals("roster", StringComparison.OrdinalIgnoreCase))
                {
                    return value.Trim() is { Length: > 0 } name ? name : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treated as no choice made.
        }

        return null;
    }

    public static Result<RosterFile> Open(RosterSource source, GameFileIndex? index)
    {
        try
        {
            if (source.SavePath is not null)
            {
                return RosterFile.OpenSave(File.ReadAllBytes(source.SavePath));
            }

            GameFile? file = index?.Find("db/nhlng.db");
            if (file is null)
            {
                return Result.Failure<RosterFile>("The game's database wasn't found.");
            }

            Result<byte[]> data = index!.ReadDecoded(file.Winner);
            return data.IsSuccess ? RosterFile.OpenDatabase(data.Value!) : Result.Failure<RosterFile>(data.Error!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<RosterFile>($"Couldn't read that roster: {ex.Message}", ex);
        }
    }
}
