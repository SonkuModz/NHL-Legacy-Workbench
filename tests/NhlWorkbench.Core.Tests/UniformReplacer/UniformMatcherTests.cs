using NhlWorkbench.Core.Tests.TestData;
using NhlWorkbench.Core.UniformReplacer;
using Xunit;

namespace NhlWorkbench.Core.Tests.UniformReplacer;

public class UniformMatcherTests
{
    private static UniformSourceSet MakeSource(UniformCategory category, string stem) => new(
        category,
        stem,
        Path.Combine(Path.GetTempPath(), stem),
        "0",
        "2",
        new Dictionary<UniformSlotKind, string> { [UniformSlotKind.ColorMap] = "dummy.dds" });

    [Fact]
    public void Match_DestinationExists_ReturnsPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformMatcherTests_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string destPath = Path.Combine(root, "texlib_1_0_2.rx2");
            File.WriteAllBytes(destPath, Array.Empty<byte>());

            UniformMatchResult result = UniformMatcher.Match(MakeSource(UniformCategory.Pant, "texlib_1_0_2"), root);

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
        string root = Path.Combine(Path.GetTempPath(), "UniformMatcherTests_" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            UniformMatchResult result = UniformMatcher.Match(MakeSource(UniformCategory.Pant, "texlib_1_9_9"), root);

            Assert.Null(result.DestinationPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MatchAll_GroupsByCategoryAndUsesEachCategorysOwnDestinationFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformMatcherTests_" + Guid.NewGuid());
        try
        {
            string pantDest = Path.Combine(root, "pant", "texlib_1_0_2.rx2");
            Directory.CreateDirectory(Path.GetDirectoryName(pantDest)!);
            File.WriteAllBytes(pantDest, Array.Empty<byte>());

            string sockDest = Path.Combine(root, "sock", "sock_1_0_2_cm.rx2");
            Directory.CreateDirectory(Path.GetDirectoryName(sockDest)!);
            File.WriteAllBytes(sockDest, Array.Empty<byte>());

            var sources = new[]
            {
                MakeSource(UniformCategory.Pant, "texlib_1_0_2"),
                MakeSource(UniformCategory.Sock, "sock_1_0_2_cm"),
            };

            IReadOnlyList<UniformMatchResult> results = UniformMatcher.MatchAll(sources, root);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.NotNull(r.DestinationPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MatchAll_RealTrees_EveryPantAndSockSourceResolvesToADestination()
    {
        if (!SampleFileCatalog.UniformAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<UniformSourceSet> sources = UniformSourceScanner.ScanAll(SampleFileCatalog.UniformSourceRoot);
        IReadOnlyList<UniformMatchResult> results = UniformMatcher.MatchAll(sources, SampleFileCatalog.UniformDestinationRoot);

        var pantResults = results.Where(r => r.Source.Category == UniformCategory.Pant).ToList();
        var sockResults = results.Where(r => r.Source.Category == UniformCategory.Sock).ToList();

        Assert.Equal(78, pantResults.Count);
        Assert.Equal(78, sockResults.Count);
        Assert.All(pantResults, r => Assert.NotNull(r.DestinationPath));
        Assert.All(sockResults, r => Assert.NotNull(r.DestinationPath));
    }
}
