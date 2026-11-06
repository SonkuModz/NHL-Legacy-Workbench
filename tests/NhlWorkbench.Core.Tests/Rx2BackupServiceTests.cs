using NhlWorkbench.Core.Backup;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class Rx2BackupServiceTests
{
    private static (Rx2BackupService Service, string SourcePath, string BackupRoot) MakeSandbox()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"Rx2BackupServiceTests_{Guid.NewGuid()}");
        string backupRoot = Path.Combine(sandbox, "backups");
        Directory.CreateDirectory(sandbox);

        string sourcePath = Path.Combine(sandbox, "stadium_0_container_0.rx2");
        File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3, 4 });

        return (new Rx2BackupService(backupRoot), sourcePath, backupRoot);
    }

    [Fact]
    public void CreateBackup_CopiesCurrentFileContentsToTheBackupFolder()
    {
        (Rx2BackupService service, string sourcePath, string backupRoot) = MakeSandbox();

        Rx2BackupEntry entry = service.CreateBackup(sourcePath);

        Assert.True(File.Exists(entry.BackupPath));
        Assert.Equal(sourcePath, entry.OriginalPath);
        Assert.Equal(backupRoot, Path.GetDirectoryName(entry.BackupPath));
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(entry.BackupPath));
    }

    [Fact]
    public void CreateBackup_CalledMultipleTimes_KeepsEveryVersionInHistoryMostRecentFirst()
    {
        (Rx2BackupService service, string sourcePath, _) = MakeSandbox();

        Rx2BackupEntry first = service.CreateBackup(sourcePath);

        File.WriteAllBytes(sourcePath, new byte[] { 9, 9, 9 });
        Rx2BackupEntry second = service.CreateBackup(sourcePath);

        IReadOnlyList<Rx2BackupEntry> history = service.GetHistory(sourcePath);

        Assert.Equal(2, history.Count);
        Assert.Equal(second, history[0]);
        Assert.Equal(first, history[1]);
        Assert.NotEqual(File.ReadAllBytes(first.BackupPath), File.ReadAllBytes(second.BackupPath));
    }

    [Fact]
    public void GetHistory_ForAPathNeverBackedUp_ReturnsEmpty()
    {
        (Rx2BackupService service, string sourcePath, _) = MakeSandbox();

        Assert.Empty(service.GetHistory(sourcePath));
    }

    [Fact]
    public void Restore_OverwritesTheOriginalFileWithTheBackedUpContents()
    {
        (Rx2BackupService service, string sourcePath, _) = MakeSandbox();

        Rx2BackupEntry entry = service.CreateBackup(sourcePath);
        File.WriteAllBytes(sourcePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }); // simulate a bad write

        service.Restore(entry);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(sourcePath));
    }

    /// <summary>
    /// Reproduces the real bug hit by a full "Import All" in the Uniform Replacer (~220 sets):
    /// batch importers call CreateBackup concurrently from a Parallel.ForEach, one call per
    /// destination file. _historyByPath is a plain Dictionary -- without the lock added to fix
    /// this, concurrent writes to *different* keys still corrupted its internal bucket array,
    /// throwing IndexOutOfRangeException on nearly every call. This test fails intermittently but
    /// reliably (not always on every run, since it's a timing-dependent race) without that lock.
    /// </summary>
    [Fact]
    public void CreateBackup_CalledConcurrentlyForManyDifferentFiles_NeverThrowsAndRecordsEveryEntry()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"Rx2BackupServiceTests_{Guid.NewGuid()}");
        string backupRoot = Path.Combine(sandbox, "backups");
        Directory.CreateDirectory(sandbox);
        var service = new Rx2BackupService(backupRoot);

        const int fileCount = 200;
        string[] paths = Enumerable.Range(0, fileCount)
            .Select(i =>
            {
                string path = Path.Combine(sandbox, $"file_{i}.rx2");
                File.WriteAllBytes(path, new byte[] { (byte)i });
                return path;
            })
            .ToArray();

        Parallel.ForEach(paths, path => service.CreateBackup(path));

        Assert.All(paths, path => Assert.Single(service.GetHistory(path)));
    }
}
