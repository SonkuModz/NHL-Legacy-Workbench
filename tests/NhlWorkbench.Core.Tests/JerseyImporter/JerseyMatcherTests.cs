using NhlWorkbench.Core.JerseyImporter;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests.JerseyImporter;

public class JerseyMatcherTests
{
    private static JerseySourceSet MakeSource(string stem) => new(
        "0",
        "2",
        stem,
        Path.Combine(Path.GetTempPath(), stem),
        new Dictionary<JerseySlotKind, string> { [JerseySlotKind.FontCm] = "dummy.dds" });

    [Fact]
    public void Match_DestinationExists_ReturnsPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseyMatcherTests_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string destPath = Path.Combine(root, "texlib_1_0_2.rx2");
            File.WriteAllBytes(destPath, Array.Empty<byte>());

            JerseyMatchResult result = JerseyMatcher.Match(MakeSource("texlib_1_0_2"), root);

            Assert.Equal(destPath, result.DestinationPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Match_NoDestination_ReturnsNullWithoutThrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseyMatcherTests_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            JerseyMatchResult result = JerseyMatcher.Match(MakeSource("texlib_1_9_9"), root);

            Assert.Null(result.DestinationPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MatchAll_RealTrees_EveryRealSourceSetResolvesToADestination()
    {
        if (!SampleFileCatalog.JerseyAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<JerseySourceSet> sources = JerseySourceScanner.Scan(SampleFileCatalog.JerseySourceRoot);
        IReadOnlyList<JerseyMatchResult> results = JerseyMatcher.MatchAll(sources, SampleFileCatalog.JerseyDestinationRoot);

        Assert.Equal(78, results.Count);
        Assert.All(results, r => Assert.NotNull(r.DestinationPath));
    }
}
