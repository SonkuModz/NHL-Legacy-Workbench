using System.Text.RegularExpressions;

namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>
/// Scans the converted-DDS output folder for jersey source sets. Every jersey folder is named
/// "texlib_1_{TeamId}_{JerseyVariant}" (confirmed: all 78 real folders use the "texlib_1_" prefix;
/// none use "texlib_0_") and contains up to 6 files named "{FolderStem}.dds" (Font CM),
/// "{FolderStem}_1.dds" .. "{FolderStem}_5.dds". The glob below already excludes the "texlib_0_*"
/// prefix and the unrelated "name_{X}_{Y}_{Z}_cm" folders (each holding a single unrelated DDS with
/// no _1.._5 siblings) that live alongside the real jersey folders in the same directory.
/// Best-effort throughout: a missing root, an unreadable folder, or a malformed name is skipped,
/// never thrown.
/// </summary>
public static class JerseySourceScanner
{
    private static readonly Regex FolderNamePattern = new(@"^texlib_1_(\d+)_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<JerseySourceSet> Scan(string sourceRoot)
    {
        var results = new List<JerseySourceSet>();

        foreach (string folderPath in EnumerateDirectoriesSafe(sourceRoot))
        {
            string folderStem = Path.GetFileName(folderPath);
            Match match = FolderNamePattern.Match(folderStem);
            if (!match.Success)
            {
                continue;
            }

            string teamId = match.Groups[1].Value;
            string jerseyVariant = match.Groups[2].Value;

            var slotFiles = new Dictionary<JerseySlotKind, string>();
            foreach (string filePath in EnumerateFilesSafe(folderPath))
            {
                string fileStem = Path.GetFileNameWithoutExtension(filePath);
                if (!fileStem.StartsWith(folderStem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string suffix = fileStem.Substring(folderStem.Length);
                JerseySlotKind? kind = JerseySlotMapping.FromSourceSuffix(suffix);
                if (kind is not null)
                {
                    slotFiles[kind.Value] = filePath;
                }
            }

            if (slotFiles.Count > 0)
            {
                results.Add(new JerseySourceSet(teamId, jerseyVariant, folderStem, folderPath, slotFiles));
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Enumerable.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, "texlib_1_*", SearchOption.TopDirectoryOnly);
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

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.dds", SearchOption.TopDirectoryOnly);
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
