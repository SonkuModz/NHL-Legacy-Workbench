using NhlWorkbench.Core.JerseyImporter;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests.JerseyImporter;

public class JerseySourceScannerTests
{
    private static string CreateFile(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void Scan_FullSet_FindsAllSixSlots()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseySourceScannerTests_" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "texlib_1_0_2");
            CreateFile(folder, "texlib_1_0_2.dds");
            CreateFile(folder, "texlib_1_0_2_1.dds");
            CreateFile(folder, "texlib_1_0_2_2.dds");
            CreateFile(folder, "texlib_1_0_2_3.dds");
            CreateFile(folder, "texlib_1_0_2_4.dds");
            CreateFile(folder, "texlib_1_0_2_5.dds");

            IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(root);

            JerseySourceSet set = Assert.Single(sets);
            Assert.Equal("0", set.TeamId);
            Assert.Equal("2", set.JerseyVariant);
            Assert.Equal("texlib_1_0_2", set.FolderStem);
            Assert.Equal(6, set.SlotFiles.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_PartialSet_OnlyIncludesFilesFound()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseySourceScannerTests_" + Guid.NewGuid());
        try
        {
            string folder = Path.Combine(root, "texlib_1_5_1");
            CreateFile(folder, "texlib_1_5_1.dds");
            CreateFile(folder, "texlib_1_5_1_3.dds");

            IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(root);

            JerseySourceSet set = Assert.Single(sets);
            Assert.Equal(2, set.SlotFiles.Count);
            Assert.True(set.SlotFiles.ContainsKey(JerseySlotKind.FontCm));
            Assert.True(set.SlotFiles.ContainsKey(JerseySlotKind.JerseyCm));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ExcludesTexlibZeroPrefixAndLooseNameFolders()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseySourceScannerTests_" + Guid.NewGuid());
        try
        {
            CreateFile(Path.Combine(root, "texlib_0_1_1"), "texlib_0_1_1.dds");
            CreateFile(Path.Combine(root, "name_1_0_2_cm"), "name_1_0_2_cm.dds");
            CreateFile(Path.Combine(root, "texlib_1_9_9"), "texlib_1_9_9.dds");

            IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(root);

            JerseySourceSet set = Assert.Single(sets);
            Assert.Equal("texlib_1_9_9", set.FolderStem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_NonNumericSegments_SkippedWithoutThrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), "JerseySourceScannerTests_" + Guid.NewGuid());
        try
        {
            CreateFile(Path.Combine(root, "texlib_1_abc_2"), "texlib_1_abc_2.dds");

            IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(root);

            Assert.Empty(sets);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmptyWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "JerseySourceScannerTests_missing_" + Guid.NewGuid());

        IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(missing);

        Assert.Empty(sets);
    }

    [Fact]
    public void Scan_RealSourceTree_Finds78SetsWithTexlib102Complete()
    {
        if (!SampleFileCatalog.JerseyAssetsAvailable)
        {
            return;
        }

        IReadOnlyList<JerseySourceSet> sets = JerseySourceScanner.Scan(SampleFileCatalog.JerseySourceRoot);

        Assert.Equal(78, sets.Count);
        JerseySourceSet? set = sets.FirstOrDefault(s => s.FolderStem == "texlib_1_0_2");
        Assert.NotNull(set);
        Assert.Equal(6, set!.SlotFiles.Count);
    }
}
