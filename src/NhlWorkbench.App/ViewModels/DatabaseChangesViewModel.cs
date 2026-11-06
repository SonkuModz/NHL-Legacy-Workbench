using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

/// <summary>Which of a row's cells differ, for the grid's highlight. The grid binds to it by column number.</summary>
public sealed class CellFlags(HashSet<int>? columns)
{
    public static readonly CellFlags None = new(null);

    public bool this[int column] => columns?.Contains(column) == true;
}

/// <summary>A changed cell in the list of every change.</summary>
public sealed record DatabaseChangeRow(DatabaseCellChange Cell)
{
    public string Where => $"{Cell.TableName} · row {Cell.Row}";

    public string Label => Cell.RowLabel;

    public string Column => Cell.ColumnName;

    public string Now => Cell.Now;

    public string Was => Cell.Original;
}

/// <summary>The Database page's comparison with the game's own copy: what's different, and putting it back.</summary>
public partial class DatabaseViewModel
{
    private GameDatabase? _original;
    private DatabaseChanges _changes = DatabaseChanges.None;
    private Dictionary<int, HashSet<int>> _changedHere = [];
    private List<DatabaseChangeRow> _allChangeRows = [];
    private int _changesVersion;
    private Task _changesTask = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangesHeading))]
    private bool _isChangesOpen;

    [ObservableProperty]
    private bool _onlyChangedRows;

    [ObservableProperty]
    private string _changeQuery = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DatabaseChangeRow> _changeRows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PutBackTableCommand))]
    private int _changedInThisTable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(ChangesHeading))]
    private int _changedInAll;

    public bool HasChanges => ChangedInAll > 0;

    /// <summary>Raised to bring a cell into view after going to a change.</summary>
    public event EventHandler<(DatabaseRow Row, int Column)>? CellWanted;

    private string ComparedWith => Source?.GamePath is null ? "the roster your mod started from" : "the game's own copy";

    public string ChangesSummary =>
        $"{Cells(ChangedInThisTable)} changed in this table, {ChangedInAll:N0}{(_changes.IsCut ? "+" : string.Empty)} in this database, compared with {ComparedWith}.";

    public string ChangesHeading =>
        $"{Cells(ChangedInAll)}{(_changes.IsCut ? " or more" : string.Empty)} differ from {ComparedWith}"
        + (_changes.IsCut ? $". Showing the first {DatabaseComparer.MostCells:N0}." : ".")
        + (_changes.Notes.Count > 0 ? " " + string.Join(" ", _changes.Notes.Take(3)) : string.Empty);

    private static string Cells(int count) => $"{count:N0} cell{(count == 1 ? string.Empty : "s")}";

    /// <summary>For one row of the grid. Never null, so the highlight's binding always has something to read.</summary>
    internal CellFlags FlagsFor(int row) =>
        _changedHere.TryGetValue(row, out HashSet<int>? columns) ? new CellFlags(columns) : CellFlags.None;

    partial void OnOnlyChangedRowsChanged(bool value) => BuildRows();

    partial void OnChangeQueryChanged(string value) => FilterChangeRows();

    private void ForgetChanges()
    {
        _changesVersion++;
        _original = null;
        ShowChanges(DatabaseChanges.None);
    }

    /// <summary>Compares a snapshot away from the page, so edits can carry on while it runs.</summary>
    private async Task RefreshChangesAsync()
    {
        int version = ++_changesVersion;
        if (_database is not { } database || Source is not { } source)
        {
            ShowChanges(DatabaseChanges.None);
            return;
        }

        byte[] snapshot = database.DatabaseBytes();
        string? gamePath = source.GamePath;
        ModProject? project = _main.Project;
        GameFileIndex? index = _main.Index;
        GameDatabase? known = _original;
        (GameDatabase? original, DatabaseChanges changes) = await Task.Run(() =>
        {
            GameDatabase? other = known ?? ReadOriginal(gamePath, project, index);
            Result<GameDatabase> now = GameDatabase.Open(snapshot);
            return other is null || !now.IsSuccess ? (other, DatabaseChanges.None) : (other, DatabaseComparer.Compare(now.Value!, other));
        });

        if (version != _changesVersion)
        {
            return;
        }

        _original = original;
        ShowChanges(changes);
    }

    private static GameDatabase? ReadOriginal(string? gamePath, ModProject? project, GameFileIndex? index)
    {
        try
        {
            Result<byte[]> data;
            if (gamePath is null)
            {
                data = project is { HasRoster: true } ? project.ReadRosterOriginal() : Result.Failure<byte[]>("No roster.");
            }
            else
            {
                GameFile? file = index?.Find(gamePath);
                GameFileCopy? copy = file is null ? null : TextureCatalog.OriginalCopy(file);
                data = copy is null ? Result.Failure<byte[]>("No game copy.") : index!.ReadDecoded(copy);
            }

            return data.IsSuccess && GameDatabase.Open(data.Value!) is { IsSuccess: true } opened ? opened.Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowChanges(DatabaseChanges changes)
    {
        _changes = changes;
        _allChangeRows = changes.Cells.Select(c => new DatabaseChangeRow(c)).ToList();
        FilterChangeRows();
        ChangedInAll = changes.Cells.Count;
        Dictionary<string, int> perTable = changes.Cells.GroupBy(c => c.TableId).ToDictionary(g => g.Key, g => g.Count());
        foreach (DatabaseTableEntry entry in _allTables)
        {
            entry.ChangeCount = perTable.GetValueOrDefault(entry.Table.Id);
        }

        if (ChangedInAll == 0)
        {
            IsChangesOpen = false;
            OnlyChangedRows = false;
        }

        FindChangesHere();
        if (OnlyChangedRows)
        {
            BuildRows();
        }
        else
        {
            foreach (DatabaseRow row in Rows)
            {
                row.RefreshChanged();
            }
        }

        OnPropertyChanged(nameof(ChangesHeading));
    }

    /// <summary>Picks out the table on show's changes, for the highlight and the summary's count.</summary>
    private void FindChangesHere()
    {
        var here = new Dictionary<int, HashSet<int>>();
        int count = 0;
        if (SelectedTable?.Table.Id is { } id)
        {
            foreach (DatabaseCellChange cell in _changes.Cells.Where(c => c.TableId == id))
            {
                if (!here.TryGetValue(cell.Row, out HashSet<int>? columns))
                {
                    here[cell.Row] = columns = [];
                }

                columns.Add(cell.ColumnIndex);
                count++;
            }
        }

        _changedHere = here;
        ChangedInThisTable = count;
        OnPropertyChanged(nameof(ChangesSummary));
    }

    private void FilterChangeRows()
    {
        string[] words = ChangeQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ChangeRows = words.Length == 0
            ? _allChangeRows
            : _allChangeRows.Where(r => words.All(w =>
                r.Where.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
                || r.Column.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Now.Contains(w, StringComparison.OrdinalIgnoreCase)
                || r.Was.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    [RelayCommand]
    private void ToggleChanges() => IsChangesOpen = !IsChangesOpen;

    /// <summary>Puts one cell back to what the game's copy holds.</summary>
    [RelayCommand]
    public void PutBackCell(DatabaseChangeRow? row)
    {
        if (row is null || _original is not { } original || _database is not { } database || !_main.EnsureProject())
        {
            return;
        }

        string? error = PutBack(database, original, row.Cell);
        Problem = error;
        if (error is not null)
        {
            _main.Status = error;
            return;
        }

        RefreshOnShow(row.Cell.TableId);
        QueueSave();
    }

    private bool CanPutBackTable() => ChangedInThisTable > 0;

    /// <summary>Puts every changed cell in the table on show back.</summary>
    [RelayCommand(CanExecute = nameof(CanPutBackTable))]
    public void PutBackTable()
    {
        if (SelectedTable?.Table is not { } table || _original is not { } original || _database is not { } database || !_main.EnsureProject())
        {
            return;
        }

        List<DatabaseCellChange> cells = _changes.Cells.Where(c => c.TableId == table.Id).ToList();
        if (!_main.IsWalking
            && !Dialogs.Confirm("Put this table back?", $"{Cells(cells.Count)} in {table.Name} go back to what's in {ComparedWith}.", "Put them back"))
        {
            return;
        }

        string? error = cells.Select(c => PutBack(database, original, c)).FirstOrDefault(e => e is not null);
        Problem = error;
        RefreshOnShow(table.Id);
        if (error is not null)
        {
            _main.Status = error;
        }

        QueueSave();
    }

    private static string? PutBack(GameDatabase database, GameDatabase original, DatabaseCellChange cell)
    {
        if (database.Tables.FirstOrDefault(t => t.Id == cell.TableId) is not { } table
            || original.Tables.FirstOrDefault(t => t.Id == cell.TableId) is not { } theirs)
        {
            return $"{cell.TableName} isn't in both copies.";
        }

        return DatabaseComparer.PutBack(table, theirs, cell.Row, table.Columns[cell.ColumnIndex]);
    }

    private void RefreshOnShow(string tableId)
    {
        if (SelectedTable?.Table.Id == tableId)
        {
            foreach (DatabaseRow row in Rows)
            {
                row.Refresh();
            }
        }
    }

    /// <summary>Shows a changed cell in its table.</summary>
    [RelayCommand]
    public void GoToChange(DatabaseChangeRow? row)
    {
        if (row is null || _allTables.FirstOrDefault(t => t.Table.Id == row.Cell.TableId) is not { } entry)
        {
            return;
        }

        IsChangesOpen = false;
        TableQuery = string.Empty;
        SelectedTable = entry;
        RowQuery = string.Empty;
        if (Rows.FirstOrDefault(r => r.Number == row.Cell.Row) is { } shown)
        {
            CellWanted?.Invoke(this, (shown, row.Cell.ColumnIndex));
        }
    }
}
