using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Iso;

namespace NhlWorkbench.Core.GameFiles;

/// <summary>
/// Turns a path the user picked into game file layers. Understands a disc image, an extracted disc
/// folder, and a PS3 recomp install.
///
/// For an install it reads <c>mods\&lt;name&gt;\USRDIR</c>, <c>data\update\USRDIR</c> and the disc files
/// directly. It deliberately skips <c>data\hdd0</c>: that folder is the launcher's hard-linked output,
/// rebuilt before every Play, and doesn't say which mod a file came from.
/// </summary>
public static class GameInstallLocator
{
    private const string SettingsFileName = "nhl_settings.ini";

    /// <summary>Works out what <paramref name="path"/> is and indexes it.</summary>
    public static Result<GameFileIndex> Open(string path)
    {
        Result<IReadOnlyList<GameLayer>> layers = ResolveLayers(path);
        return layers.IsSuccess
            ? GameFileIndex.Build(layers.Value!)
            : Result.Failure<GameFileIndex>(layers.Error!, layers.Exception);
    }

    public static Result<IReadOnlyList<GameLayer>> ResolveLayers(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)
                    ? Disc(path).ToList()
                    : Result.Failure<IReadOnlyList<GameLayer>>($"{path} isn't a disc image or a folder.");
            }

            if (!Directory.Exists(path))
            {
                return Result.Failure<IReadOnlyList<GameLayer>>($"{path} doesn't exist.");
            }

            if (IsRecompInstall(path))
            {
                return RecompInstall(path);
            }

            string? usrDir = FindUsrDir(path);
            if (usrDir is not null)
            {
                return Success(new GameLayer(GameLayerKind.Disc, "Disc", new DirectoryGameFileSource(usrDir, usrDir)));
            }

            // The release zip extracts to NHL-Legacy-Native_v1.0.0\NHL Legacy Native, so people often pick the outer folder.
            string[] nested = Directory.EnumerateDirectories(path).Where(IsRecompInstall).ToArray();
            return nested.Length == 1
                ? RecompInstall(nested[0])
                : Result.Failure<IReadOnlyList<GameLayer>>($"{path} doesn't look like a recomp install or a disc folder (no PS3_GAME\\USRDIR).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<IReadOnlyList<GameLayer>>($"Can't read {path}: {ex.Message}", ex);
        }
    }

    /// <summary>The recomp install at <paramref name="path"/> or one folder inside it, or null if there isn't one.</summary>
    public static string? FindInstallRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            if (IsRecompInstall(path))
            {
                return path;
            }

            string[] nested = Directory.EnumerateDirectories(path).Where(IsRecompInstall).ToArray();
            return nested.Length == 1 ? nested[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The mods the launcher has switched on, best first.</summary>
    public static IReadOnlyList<string> EnabledMods(string installRoot)
    {
        try
        {
            return EnabledModsBestFirst(installRoot, ReadSettings(Path.Combine(installRoot, SettingsFileName))).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Changes when a mod is switched, moved or reinstalled, so the app knows to read the game files again.</summary>
    public static string EnabledModsFingerprint(string installRoot)
    {
        try
        {
            return string.Join('|', EnabledMods(installRoot).Select(name =>
            {
                string folder = Path.Combine(installRoot, "mods", name);
                string usrDir = Path.Combine(folder, "USRDIR");
                return $"{name}:{Directory.GetLastWriteTimeUtc(folder).Ticks}:{Directory.GetLastWriteTimeUtc(usrDir).Ticks}";
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool IsRecompInstall(string path) =>
        File.Exists(Path.Combine(path, SettingsFileName)) || Directory.Exists(Path.Combine(path, "data", "update"));

    private static Result<IReadOnlyList<GameLayer>> RecompInstall(string installDir)
    {
        Dictionary<string, Dictionary<string, string>> settings = ReadSettings(Path.Combine(installDir, SettingsFileName));
        var layers = new List<GameLayer>();
        foreach (string name in EnabledModsBestFirst(installDir, settings))
        {
            string modUsrDir = Path.Combine(installDir, "mods", name, "USRDIR");
            if (Directory.Exists(modUsrDir))
            {
                layers.Add(new GameLayer(GameLayerKind.Mod, name, new DirectoryGameFileSource(modUsrDir, modUsrDir)));
            }
        }

        string updateUsrDir = Path.Combine(installDir, "data", "update", "USRDIR");
        if (Directory.Exists(updateUsrDir))
        {
            layers.Add(new GameLayer(GameLayerKind.Update, "Update 1.01", new DirectoryGameFileSource(updateUsrDir, updateUsrDir)));
        }

        Result<GameLayer> disc = InstallDisc(installDir, settings);
        if (!disc.IsSuccess)
        {
            return Result.Failure<IReadOnlyList<GameLayer>>(disc.Error!, disc.Exception);
        }

        layers.Add(disc.Value!);
        return Success(layers.ToArray());
    }

    /// <summary>An ISO install extracts to game\PS3_GAME. A folder install keeps using the source folder.</summary>
    private static Result<GameLayer> InstallDisc(string installDir, Dictionary<string, Dictionary<string, string>> settings)
    {
        string extracted = Path.Combine(installDir, "game", "PS3_GAME", "USRDIR");
        if (Directory.Exists(extracted))
        {
            return Result.Success(new GameLayer(GameLayerKind.Disc, "Disc", new DirectoryGameFileSource(extracted, extracted)));
        }

        string? source = Get(settings, "launcher", "game_source");
        if (string.IsNullOrEmpty(source))
        {
            return Result.Failure<GameLayer>($"{installDir} has no disc files (no game\\PS3_GAME\\USRDIR and no game_source setting).");
        }

        string resolved = Path.GetFullPath(source, installDir);
        if (File.Exists(resolved))
        {
            Result<GameLayer> disc = Disc(resolved);
            return disc;
        }

        string? usrDir = Directory.Exists(resolved) ? FindUsrDir(resolved) : null;
        return usrDir is not null
            ? Result.Success(new GameLayer(GameLayerKind.Disc, "Disc", new DirectoryGameFileSource(usrDir, usrDir)))
            : Result.Failure<GameLayer>($"The install's game_source, {source}, can't be found.");
    }

    private static Result<GameLayer> Disc(string isoPath)
    {
        Result<Iso9660Image> image = Iso9660Image.Open(isoPath);
        return image.IsSuccess
            ? Result.Success(new GameLayer(GameLayerKind.Disc, "Disc", new IsoGameFileSource(image.Value!, isoPath)))
            : Result.Failure<GameLayer>(image.Error!, image.Exception);
    }

    /// <summary>Matches the launcher's list_mods: names from mod_order first, then the rest alphabetically. A mod is on unless set to 0.</summary>
    private static IEnumerable<string> EnabledModsBestFirst(string installDir, Dictionary<string, Dictionary<string, string>> settings)
    {
        string modsDir = Path.Combine(installDir, "mods");
        if (!Directory.Exists(modsDir))
        {
            return [];
        }

        string[] order = (Get(settings, "launcher", "mod_order") ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);
        int Rank(string name)
        {
            int index = Array.FindIndex(order, o => o.Equals(name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : order.Length;
        }

        return new DirectoryInfo(modsDir).EnumerateDirectories()
            .Select(d => d.Name)
            .Where(name => !name.StartsWith('.') && Get(settings, "mods", name) != "0")
            .OrderBy(Rank)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Accepts a folder holding PS3_GAME, the PS3_GAME folder, or USRDIR itself.</summary>
    private static string? FindUsrDir(string folder)
    {
        string[] candidates =
        [
            Path.Combine(folder, "PS3_GAME", "USRDIR"),
            Path.Combine(folder, "USRDIR"),
        ];
        string? found = candidates.FirstOrDefault(Directory.Exists);
        if (found is not null)
        {
            return found;
        }

        return new DirectoryInfo(folder).Name.Equals("USRDIR", StringComparison.OrdinalIgnoreCase) ? folder : null;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadSettings(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return sections;
        }

        string section = string.Empty;
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = (close > 0 ? line[1..close] : line[1..]).Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            if (!sections.TryGetValue(section, out Dictionary<string, string>? values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add(section, values);
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return sections;
    }

    private static string? Get(Dictionary<string, Dictionary<string, string>> settings, string section, string key) =>
        settings.TryGetValue(section, out Dictionary<string, string>? values) ? values.GetValueOrDefault(key) : null;

    private static Result<IReadOnlyList<GameLayer>> Success(params GameLayer[] layers) =>
        Result.Success<IReadOnlyList<GameLayer>>(layers);

    private static Result<IReadOnlyList<GameLayer>> ToList(this Result<GameLayer> layer) =>
        layer.IsSuccess ? Success(layer.Value!) : Result.Failure<IReadOnlyList<GameLayer>>(layer.Error!, layer.Exception);
}
