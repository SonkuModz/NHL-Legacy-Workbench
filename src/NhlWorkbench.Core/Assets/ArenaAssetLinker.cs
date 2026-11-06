using System.Text.RegularExpressions;

namespace NhlWorkbench.Core.Assets;

/// <summary>Groups stadium/center-logo/jumbotron RX2 files by the numeric arena ID embedded in each
/// filename, so the Arena Texture Editor never requires the user to manually locate related assets.
/// Every lookup here is best-effort: a missing directory or a stadium file with no matching logo/
/// jumbotron is a normal, expected outcome, never a thrown exception.</summary>
public static class ArenaAssetLinker
{
    private static readonly Regex StadiumFilePattern = new(@"^stadium_(\d+)_container_0\.rx2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CenterLogoFilePattern = new(@"^centerlogo_(\d+)_cm\.rx2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JumbotronFilePattern = new(@"^texlib_(\d+)\.rx2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Shared, non-numbered ice-surface containers that live alongside the per-arena stadium
    /// files but apply to every arena rather than just one.</summary>
    private static readonly string[] SharedIceSurfaceNames =
    {
        "ice_nhl_container_0.rx2",
        "ice_intl_container_0.rx2",
        "ice_nhl_full_lm_container_0.rx2",
    };

    public static ArenaAssetLinkResult Link(string stadiumDir, string centerLogoDir, string jumbotronDir)
    {
        Dictionary<int, string> centerLogosById = IndexByCapturedId(centerLogoDir, CenterLogoFilePattern);
        Dictionary<int, string> jumbotronsById = IndexByCapturedId(jumbotronDir, JumbotronFilePattern);

        var arenas = new List<ArenaAssetGroup>();
        var sharedIceSurfaces = new List<SharedIceSurface>();

        foreach (string path in EnumerateFilesSafe(stadiumDir))
        {
            string fileName = Path.GetFileName(path);

            if (SharedIceSurfaceNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                sharedIceSurfaces.Add(new SharedIceSurface(Path.GetFileNameWithoutExtension(fileName), path));
                continue;
            }

            Match match = StadiumFilePattern.Match(fileName);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int arenaId))
            {
                continue;
            }

            centerLogosById.TryGetValue(arenaId, out string? centerLogoPath);
            jumbotronsById.TryGetValue(arenaId, out string? jumbotronPath);

            arenas.Add(new ArenaAssetGroup(arenaId, path, centerLogoPath, jumbotronPath));
        }

        arenas = arenas.OrderBy(a => a.ArenaId).ToList();

        return new ArenaAssetLinkResult(arenas, sharedIceSurfaces);
    }

    private static Dictionary<int, string> IndexByCapturedId(string directory, Regex pattern)
    {
        var byId = new Dictionary<int, string>();
        foreach (string path in EnumerateFilesSafe(directory))
        {
            Match match = pattern.Match(Path.GetFileName(path));
            if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
            {
                byId[id] = path;
            }
        }

        return byId;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Enumerable.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.rx2", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return Enumerable.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }
}
