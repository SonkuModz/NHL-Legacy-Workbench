namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>
/// Matches a source set to its destination RX2 container. Matching is exact-stem equality
/// (source folder "texlib_1_A_B" == destination file "texlib_1_A_B.rx2") -- confirmed against the
/// real destination tree that every one of the 78 real source sets resolves this way, so no fuzzy
/// matching is needed.
/// </summary>
public static class JerseyMatcher
{
    public static JerseyMatchResult Match(JerseySourceSet source, string destinationRoot)
    {
        string candidatePath = Path.Combine(destinationRoot, source.FolderStem + ".rx2");
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

        return new JerseyMatchResult(source, exists ? candidatePath : null);
    }

    public static IReadOnlyList<JerseyMatchResult> MatchAll(IEnumerable<JerseySourceSet> sources, string destinationRoot)
        => sources.Select(s => Match(s, destinationRoot)).ToList();
}
