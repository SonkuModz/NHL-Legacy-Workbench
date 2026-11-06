namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>
/// Matches a source set to its destination RX2 container. Matching is exact-stem equality (e.g.
/// source folder "texlib_1_0_2" == destination file "texlib_1_0_2.rx2") -- confirmed against the
/// real destination tree for all three categories, same as the Jersey Importer's matcher.
/// </summary>
public static class UniformMatcher
{
    public static UniformMatchResult Match(UniformSourceSet source, string destinationCategoryRoot)
    {
        string candidatePath = Path.Combine(destinationCategoryRoot, source.FolderStem + ".rx2");
        bool exists;
        try
        {
            exists = File.Exists(candidatePath);
        }
        catch (IOException)
        {
            exists = false;
        }
        catch (UnauthorizedAccessException)
        {
            exists = false;
        }

        return new UniformMatchResult(source, exists ? candidatePath : null);
    }

    public static IReadOnlyList<UniformMatchResult> MatchAll(IEnumerable<UniformSourceSet> sources, string destinationRenderingRoot)
    {
        var results = new List<UniformMatchResult>();
        foreach (IGrouping<UniformCategory, UniformSourceSet> group in sources.GroupBy(s => s.Category))
        {
            UniformCategoryDefinition definition = UniformCategoryDefinition.All.First(c => c.Category == group.Key);
            string categoryRoot = Path.Combine(destinationRenderingRoot, definition.FolderName);
            foreach (UniformSourceSet source in group)
            {
                results.Add(Match(source, categoryRoot));
            }
        }

        return results;
    }
}
