namespace NhlWorkbench.Core.Backup;

/// <inheritdoc cref="IRx2BackupService"/>
public sealed class Rx2BackupService : IRx2BackupService
{
    private readonly string _backupRoot;
    private readonly Dictionary<string, List<Rx2BackupEntry>> _historyByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private int _sequence;

    /// <param name="backupRoot">Defaults to <c>%LocalAppData%\NhlLegacyModdingWorkbench\backups</c>; overridable so tests don't write into the real app-data folder.</param>
    public Rx2BackupService(string? backupRoot = null)
    {
        _backupRoot = backupRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NhlLegacyModdingWorkbench",
            "backups");
        Directory.CreateDirectory(_backupRoot);
    }

    public Rx2BackupEntry CreateBackup(string sourcePath)
    {
        // The timestamp alone isn't a reliable uniqueness guarantee -- two backups of the same
        // file can land in the same millisecond in a fast loop (e.g. batch conversion), which
        // would silently overwrite an earlier snapshot instead of preserving it. An
        // interlocked sequence number guarantees a distinct filename every call regardless of
        // timing.
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        int sequence = Interlocked.Increment(ref _sequence);
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        string backupPath = Path.Combine(_backupRoot, $"{baseName}_{stamp}_{sequence}{extension}");

        File.Copy(sourcePath, backupPath, overwrite: true);

        var entry = new Rx2BackupEntry(sourcePath, backupPath, DateTime.UtcNow);
        string key = NormalizeKey(sourcePath);

        // Batch importers (Jersey Importer, Uniform Replacer) call this concurrently from a
        // Parallel.ForEach, one call per destination file -- Dictionary is not thread-safe for
        // concurrent writes even to different keys, and without this lock a large batch (~200+
        // sets) reliably corrupted _historyByPath, surfacing as "Index was outside the bounds of
        // the array" on nearly every subsequent call from any thread.
        lock (_historyLock)
        {
            if (!_historyByPath.TryGetValue(key, out List<Rx2BackupEntry>? history))
            {
                history = new List<Rx2BackupEntry>();
                _historyByPath[key] = history;
            }

            history.Insert(0, entry);
        }

        return entry;
    }

    public IReadOnlyList<Rx2BackupEntry> GetHistory(string sourcePath)
    {
        lock (_historyLock)
        {
            return _historyByPath.TryGetValue(NormalizeKey(sourcePath), out List<Rx2BackupEntry>? history)
                ? history.ToList()
                : Array.Empty<Rx2BackupEntry>();
        }
    }

    public void Restore(Rx2BackupEntry entry) => File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);

    private static string NormalizeKey(string path) => Path.GetFullPath(path);
}
