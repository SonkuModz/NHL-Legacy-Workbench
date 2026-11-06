namespace NhlWorkbench.Core.Backup;

/// <summary>One backup snapshot of a file, taken immediately before a modifying write.</summary>
public sealed record Rx2BackupEntry(string OriginalPath, string BackupPath, DateTime CreatedAtUtc);

/// <summary>
/// Backs up an RX2 file before any modifying write (mesh import, texture replace-and-save),
/// so a bad import or a bug in the writer never costs the user their only copy of the file.
/// Every call creates a new timestamped snapshot -- this is deliberately a version history,
/// not a single "first backup only" checkpoint, so the user can step back further than just
/// one edit within a session.
/// </summary>
public interface IRx2BackupService
{
    /// <summary>Copies <paramref name="sourcePath"/> to the backup folder and returns the new entry.</summary>
    Rx2BackupEntry CreateBackup(string sourcePath);

    /// <summary>Backups taken this session for <paramref name="sourcePath"/>, most recent first.</summary>
    IReadOnlyList<Rx2BackupEntry> GetHistory(string sourcePath);

    /// <summary>Copies a backup's contents back over its original path.</summary>
    void Restore(Rx2BackupEntry entry);
}
