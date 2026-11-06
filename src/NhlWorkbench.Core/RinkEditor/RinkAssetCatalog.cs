using System.Text.RegularExpressions;

namespace NhlWorkbench.Core.RinkEditor;

/// <summary>
/// Enumerates the shared centerlogo/hotspot sponsor-logo library in the icesurface folder. Unlike
/// <see cref="Assets.ArenaAssetLinker"/> (which links files to a specific arena), these libraries are
/// arena-agnostic -- a hotspot's numeric id identifies a sponsor logo (e.g. id 2 = EA Sports) shared
/// across every arena that references it, not "arena 2's hotspot". Every lookup is best-effort: a
/// missing/unreadable directory returns an empty list rather than throwing.
/// </summary>
public static class RinkAssetCatalog
{
    private static readonly Regex CenterLogoPattern = new(@"^centerlogo_(\d+)_cm\.rx2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HotspotPattern = new(@"^ice_hotspot_([012])_(\d+)_cm\.rx2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<RinkAssetCatalogEntry> Enumerate(string icesurfaceDir)
    {
        var entries = new List<RinkAssetCatalogEntry>();

        foreach (string path in EnumerateFilesSafe(icesurfaceDir))
        {
            string fileName = Path.GetFileName(path);

            Match centerLogoMatch = CenterLogoPattern.Match(fileName);
            if (centerLogoMatch.Success && int.TryParse(centerLogoMatch.Groups[1].Value, out int logoId))
            {
                entries.Add(new RinkAssetCatalogEntry(logoId, fileName, path, RinkAssetKind.CenterLogo, HotspotVariant: null));
                continue;
            }

            Match hotspotMatch = HotspotPattern.Match(fileName);
            if (hotspotMatch.Success
                && int.TryParse(hotspotMatch.Groups[1].Value, out int variant)
                && int.TryParse(hotspotMatch.Groups[2].Value, out int hotspotId))
            {
                entries.Add(new RinkAssetCatalogEntry(hotspotId, fileName, path, RinkAssetKind.Hotspot, HotspotVariant: variant));
            }
        }

        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.HotspotVariant)
            .ThenBy(e => e.Id)
            .ToList();
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
