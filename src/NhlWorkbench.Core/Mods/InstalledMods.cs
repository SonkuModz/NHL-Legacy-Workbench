using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

/// <summary>A mod in the game's launcher, and how it sits with the mod being worked on.</summary>
/// <param name="Name">Its folder name, which is the name the launcher shows.</param>
/// <param name="Position">Its place in the launcher's list, from 0 at the top. The top mod wins any clash.</param>
/// <param name="IsEnabled">False if it's turned off in the launcher.</param>
/// <param name="FileCount">How many game files it changes.</param>
/// <param name="HasRoster">True if it carries a roster save.</param>
/// <param name="IsRosterInUse">True if the launcher has its roster chosen.</param>
/// <param name="IsMine">True if this is the installed copy of the mod being worked on.</param>
/// <param name="Shared">Game files it and the mod being worked on both change.</param>
public sealed record InstalledMod(string Name, int Position, bool IsEnabled, int FileCount, bool HasRoster, bool IsRosterInUse, bool IsMine, IReadOnlyList<string> Shared);

/// <summary>A game file an installed mod carries.</summary>
public sealed record InstalledModFile(string GamePath, string FullPath, long Size);

/// <summary>Reads what the game's launcher has installed. It only reads: the launcher owns these files.</summary>
public static class InstalledMods
{
    /// <summary>Every installed mod in the launcher's order, top first.</summary>
    public static IReadOnlyList<InstalledMod> Read(string installRoot, ModProject? mine)
    {
        var mods = new List<InstalledMod>();
        try
        {
            string folder = Path.Combine(installRoot, "mods");
            if (!Directory.Exists(folder))
            {
                return mods;
            }

            Dictionary<string, Dictionary<string, string>> settings = ReadSettings(Path.Combine(installRoot, "nhl_settings.ini"));
            Dictionary<string, string> launcher = settings.GetValueOrDefault("launcher") ?? [];
            Dictionary<string, string> enabled = settings.GetValueOrDefault("mods") ?? [];
            List<string> order = launcher.GetValueOrDefault("mod_order", string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            string rosterInUse = launcher.GetValueOrDefault("roster", string.Empty);
            HashSet<string> myFiles = mine?.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            var unordered = new List<InstalledMod>();
            foreach (string path in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(path);
                string usrdir = Path.Combine(path, "USRDIR");
                List<string> files = Directory.Exists(usrdir)
                    ? Directory.EnumerateFiles(usrdir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(usrdir, f).Replace('\\', '/').ToLowerInvariant()).ToList()
                    : [];
                bool isMine = mine is not null && name.Equals(mine.Name, StringComparison.OrdinalIgnoreCase);
                int position = order.FindIndex(o => o.Equals(name, StringComparison.OrdinalIgnoreCase));
                unordered.Add(new InstalledMod(
                    name,
                    position < 0 ? int.MaxValue : position,
                    !enabled.TryGetValue(name, out string? on) || on != "0",
                    files.Count,
                    File.Exists(Path.Combine(path, "roster", "SYS-DATA")),
                    name.Equals(rosterInUse, StringComparison.OrdinalIgnoreCase),
                    isMine,
                    isMine ? [] : files.Where(myFiles.Contains).OrderBy(f => f, StringComparer.Ordinal).ToList()));
            }

            mods.AddRange(unordered.OrderBy(m => m.Position).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was read is still worth showing.
        }

        return mods;
    }

    /// <summary>The game files a mod carries, in path order. A name that isn't a plain folder name finds nothing.</summary>
    public static IReadOnlyList<InstalledModFile> FilesOf(string installRoot, string modName)
    {
        try
        {
            if (modName.Length == 0 || Path.GetFileName(modName) != modName)
            {
                return [];
            }

            string usrdir = Path.Combine(installRoot, "mods", modName, "USRDIR");
            return !Directory.Exists(usrdir)
                ? []
                : new DirectoryInfo(usrdir).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(f => new InstalledModFile(Path.GetRelativePath(usrdir, f.FullName).Replace('\\', '/').ToLowerInvariant(), f.FullName, f.Length))
                    .OrderBy(f => f.GamePath, StringComparer.Ordinal)
                    .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Copies files from an installed mod into the mod being worked on. The installed mod isn't changed.</summary>
    public static (int Copied, IReadOnlyList<string> Problems) CopyInto(ModProject project, IEnumerable<InstalledModFile> files, GameFileIndex? index, Action<int, int>? progress = null)
    {
        List<InstalledModFile> list = files.ToList();
        var problems = new List<string>();
        int copied = 0;
        for (int i = 0; i < list.Count; i++)
        {
            progress?.Invoke(i + 1, list.Count);
            try
            {
                Result<ProjectFile> saved = project.SetFile(list[i].GamePath, File.ReadAllBytes(list[i].FullPath), index);
                if (saved.IsSuccess)
                {
                    copied++;
                }
                else
                {
                    problems.Add(saved.Error!);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{list[i].GamePath}: {ex.Message}");
            }
        }

        return (copied, problems);
    }

    /// <summary>True if the mod being worked on would win clashes with this one. A mod not yet installed goes in at the top.</summary>
    public static bool MineWinsOver(InstalledMod other, IReadOnlyList<InstalledMod> all) =>
        all.FirstOrDefault(m => m.IsMine) is not { } mine || mine.Position < other.Position;

    private static Dictionary<string, Dictionary<string, string>> ReadSettings(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return sections;
        }

        Dictionary<string, string>? current = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line[1..^1]] = current;
            }
            else if (current is not null && line.LastIndexOf('=') is > 0 and var equals)
            {
                // Mod names can hold an equals sign, and values here never do, except in paths with a key that never does.
                string key = line[..line.IndexOf('=')].Trim();
                string lastKey = line[..equals].Trim();
                if (sections.TryGetValue("mods", out Dictionary<string, string>? mods) && ReferenceEquals(mods, current))
                {
                    current[lastKey] = line[(equals + 1)..].Trim();
                }
                else
                {
                    current[key] = line[(line.IndexOf('=') + 1)..].Trim();
                }
            }
        }

        return sections;
    }
}
