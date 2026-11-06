using NhlWorkbench.Core.Tests.TestData;
using NhlWorkbench.Core.UniformReplacer;
using Xunit;

namespace NhlWorkbench.Core.Tests.UniformReplacer;

public class UniformSourceScannerTests
{
    private static string CreateFile(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    /// <summary>Pant's Normal Map ("_1") is deliberately excluded from the category's suffix map
    /// (see <c>UniformCategoryDefinition.Pant</c>'s doc comment) -- a full 3-file source folder
    /// therefore still only yields 2 slots (CM + SM), even though the "_1" file exists on disk.</summary>
    [Fact]
    public void Scan_Pant_FullSetOnDisk_OnlyFindsColorMapAndSpecularMap()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformScannerTests_" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "texlib_1_0_2");
            CreateFile(folder, "texlib_1_0_2.dds");
            CreateFile(folder, "texlib_1_0_2_1.dds");
            CreateFile(folder, "texlib_1_0_2_2.dds");

            IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.Scan(UniformCategoryDefinition.Pant, root);

            UniformSourceSet set = Assert.Single(sets);
            Assert.Equal(UniformCategory.Pant, set.Category);
            Assert.Equal("0", set.PrimaryId);
            Assert.Equal("2", set.SecondaryId);
            Assert.Equal(2, set.SlotFiles.Count);
            Assert.True(set.SlotFiles.ContainsKey(UniformSlotKind.ColorMap));
            Assert.True(set.SlotFiles.ContainsKey(UniformSlotKind.SpecularMap));
            Assert.False(set.SlotFiles.ContainsKey(UniformSlotKind.NormalMap));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_Pant_ExcludesUnrelatedMeshFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformScannerTests_" + Guid.NewGuid());
        try
        {
            CreateFile(Path.Combine(root, "pant_0_0_0_0"), "pant_0_0_0_0.rx2");
            CreateFile(Path.Combine(root, "texlib_1_9_9"), "texlib_1_9_9.dds");

            IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.Scan(UniformCategoryDefinition.Pant, root);

            UniformSourceSet set = Assert.Single(sets);
            Assert.Equal("texlib_1_9_9", set.FolderStem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_Sock_SingleFile_HasOnlyColorMap()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformScannerTests_" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "sock_1_0_2_cm");
            CreateFile(folder, "sock_1_0_2_cm.dds");

            IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.Scan(UniformCategoryDefinition.Sock, root);

            UniformSourceSet set = Assert.Single(sets);
            Assert.Equal(UniformCategory.Sock, set.Category);
            Assert.Single(set.SlotFiles);
            Assert.True(set.SlotFiles.ContainsKey(UniformSlotKind.ColorMap));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_GoalieHelmet_SingleNumericId_NoSecondaryId()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniformScannerTests_" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "goaliehelmet_0_5_cm");
            CreateFile(folder, "goaliehelmet_0_5_cm.dds");

            IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.Scan(UniformCategoryDefinition.GoalieHelmet, root);

            UniformSourceSet set = Assert.Single(sets);
            Assert.Equal("5", set.PrimaryId);
            Assert.Null(set.SecondaryId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmptyWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "UniformScannerTests_missing_" + Guid.NewGuid());

        IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.Scan(UniformCategoryDefinition.Pant, missing);

        Assert.Empty(sets);
    }

    [Fact]
    public void ScanAll_RealTree_Finds78PantSets78SockSets64GoalieHelmetSets()
    {
        if (!SampleFileCatalog.UniformAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<UniformSourceSet> sets = UniformSourceScanner.ScanAll(SampleFileCatalog.UniformSourceRoot);

        Assert.Equal(78, sets.Count(s => s.Category == UniformCategory.Pant));
        Assert.Equal(78, sets.Count(s => s.Category == UniformCategory.Sock));
        Assert.Equal(64, sets.Count(s => s.Category == UniformCategory.GoalieHelmet));

        UniformSourceSet? pant = sets.FirstOrDefault(s => s.Category == UniformCategory.Pant && s.FolderStem == "texlib_1_0_2");
        Assert.NotNull(pant);
        Assert.Equal(2, pant!.SlotFiles.Count); // CM + SM only -- NormalMap deliberately excluded, see UniformCategoryDefinition.Pant
    }
}
