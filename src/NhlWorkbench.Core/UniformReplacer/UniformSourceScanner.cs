using System.Text.RegularExpressions;

namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>
/// Scans the converted-DDS "renderinout" output folder for uniform source sets. Mirrors
/// <c>JerseySourceScanner</c>'s shape: best-effort throughout (a missing root, an unreadable
/// subfolder, or a malformed name is skipped, never thrown), matching only the folder-name pattern
/// for the requested category so unrelated files/folders that happen to share the same parent
/// directory (e.g. "pant_*.rx2" mesh files alongside "texlib_1_*" pant texture folders) are never
/// misread as partial sets.
/// </summary>
public static class UniformSourceScanner
{
    public static IReadOnlyList<UniformSourceSet> Scan(UniformCategoryDefinition category, string categorySourceRoot)
    {
        var results = new List<UniformSourceSet>();

        foreach (string folderPath in EnumerateDirectoriesSafe(categorySourceRoot))
        {
            string folderStem = Path.GetFileName(folderPath);
            Match match = category.FolderNamePattern.Match(folderStem);
            if (!match.Success)
            {
                continue;
            }

            string primaryId = match.Groups[1].Value;
            string? secondaryId = match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : null;

            var slotFiles = new Dictionary<UniformSlotKind, string>();
            foreach (string filePath in EnumerateFilesSafe(folderPath))
            {
                string fileStem = Path.GetFileNameWithoutExtension(filePath);
                if (!fileStem.StartsWith(folderStem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string suffix = fileStem.Substring(folderStem.Length);
                UniformSlotKind? kind = category.FromSourceSuffix(suffix);
                if (kind is not null)
                {
                    slotFiles[kind.Value] = filePath;
                }
            }

            if (slotFiles.Count > 0)
            {
                results.Add(new UniformSourceSet(category.Category, folderStem, folderPath, primaryId, secondaryId, slotFiles));
            }
        }

        return results;
    }

    /// <summary>Scans every known category in one call, given the "renderinout" root -- each
    /// category's own subfolder (e.g. "renderinout/pant") is resolved from
    /// <see cref="UniformCategoryDefinition.FolderName"/>.</summary>
    public static IReadOnlyList<UniformSourceSet> ScanAll(string renderinoutRoot)
    {
        var results = new List<UniformSourceSet>();
        foreach (UniformCategoryDefinition category in UniformCategoryDefinition.All)
        {
            results.AddRange(Scan(category, Path.Combine(renderinoutRoot, category.FolderName)));
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
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);
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
