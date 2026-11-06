using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Roster;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A database that can be opened, shown as a button across the top of the side panel.</summary>
public partial class DatabaseSourceEntry : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public required string Label { get; init; }

    public required string Note { get; init; }

    /// <summary>The game file, or null for the roster save in the mod.</summary>
    public string? GamePath { get; init; }
}

/// <summary>A table in the side list.</summary>
public sealed partial class DatabaseTableEntry(DatabaseTable table) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(ChangeNote))]
    private int _changeCount;

    public DatabaseTable Table { get; } = table;

    public string Name => Table.Name;

    public string Rows => Table.RowCount.ToString("N0", CultureInfo.InvariantCulture);

    public bool HasChanges => ChangeCount > 0;

    public string ChangeNote => $"{ChangeCount:N0} changed";
}

/// <summary>A row in the grid. Its cells are read straight from the table, so they're always what's stored.</summary>
public sealed class DatabaseRow(DatabaseViewModel owner, DatabaseTable table, int row) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Number => row;

    /// <summary>Which cells differ from the game's copy.</summary>
    public CellFlags Changed => owner.FlagsFor(row);

    public string this[int column]
    {
        get => table.GetCell(row, table.Columns[column]);
        set => owner.Commit(this, table, column, value);
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

    public void RefreshChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Changed)));
}

/// <summary>
/// The Database page: any table in the game's databases, or in the mod's roster save, as an editable grid. Changes go
/// to a copy in the mod. The game's own files are only read.
/// </summary>
public partial class DatabaseViewModel : ObservableObject
{
    private const string MainDatabase = "db/nhlng.db";

    private readonly MainViewModel _main;
    private List<DatabaseTableEntry> _allTables = [];
    private GameDatabase? _database;
    private ModProject? _loadedProject;
    private bool _loadedFromMod;
    private bool _isLoaded;
    private bool _saving;
    private bool _saveAgain;
    private int _openVersion;
    private Task _opening = Task.CompletedTask;
    private Task _saveTask = Task.CompletedTask;
    private HashSet<string>? _rosterTableIds;
    private int _sortColumn = -1;
    private bool _sortDescending;

    [ObservableProperty]
    private IReadOnlyList<DatabaseSourceEntry> _sources = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAllCommand))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private DatabaseSourceEntry? _source;

    [ObservableProperty]
    private IReadOnlyList<DatabaseTableEntry> _tables = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable), nameof(TableTitle), nameof(TableNote), nameof(RosterWarning), nameof(HasRosterWarning))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand), nameof(ImportCsvCommand))]
    private DatabaseTableEntry? _selectedTable;

    [ObservableProperty]
    private string _tableQuery = string.Empty;

    [ObservableProperty]
    private string _rowQuery = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DatabaseRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAllCommand))]
    [NotifyPropertyChangedFor(nameof(WhereFrom))]
    private bool _isInMod;

    public DatabaseViewModel(MainViewModel main) => _main = main;

    /// <summary>Raised when the grid needs new columns, since a grid's columns can't be bound.</summary>
    public event EventHandler? ColumnsChanged;

    public bool HasSource => Source is not null;

    public bool HasTable => SelectedTable is not null;

    public bool HasProblem => Problem is not null;

    public DatabaseTable? CurrentTable => SelectedTable?.Table;

    public string TableTitle => SelectedTable?.Name ?? string.Empty;

    public string TableNote => SelectedTable is { } entry
        ? $"{entry.Table.RowCount:N0} rows, room for {entry.Table.MaxRows:N0} · {entry.Table.Columns.Count} columns · table id {entry.Table.Id}"
        : string.Empty;

    public string WhereFrom => Source is null ? string.Empty
        : Source.GamePath is null ? "The roster save in your mod. Changes save as you make them."
        : IsInMod ? "Your mod's copy. Changes save as you make them."
        : "The game's copy. Your first change makes a copy in your mod.";

    public bool HasRosterWarning => RosterWarning.Length > 0;

    public string RosterWarning =>
        Source?.GamePath == MainDatabase && SelectedTable is { } entry && _rosterTableIds?.Contains(entry.Table.Id) == true
            ? "Roster saves carry this table too, and the game uses the save's copy once one is loaded. A change here may not show in game, so edit the roster's copy if your mod has one."
            : string.Empty;

    partial void OnTableQueryChanged(string value) => FilterTables();

    partial void OnRowQueryChanged(string value) => BuildRows();

    partial void OnSelectedTableChanged(DatabaseTableEntry? value)
    {
        Problem = null;
        _sortColumn = -1;
        FindChangesHere();
        if (RowQuery.Length > 0)
        {
            RowQuery = string.Empty;
        }
        else
        {
            BuildRows();
        }

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the old game. The new one is read the next time the page opens.</summary>
    public void Reset()
    {
        _openVersion++;
        _isLoaded = false;
        _database = null;
        ForgetChanges();
        _rosterTableIds = null;
        _allTables = [];
        Tables = [];
        SelectedTable = null;
        Source = null;
        Sources = [];
        Message = string.Empty;
        Problem = null;
        if (_main.IsDatabasePage)
        {
            EnsureLoaded();
        }
    }

    /// <summary>Opens the main database the first time, and picks up anything the Roster page changed since.</summary>
    public void EnsureLoaded()
    {
        if (_main.Index is null)
        {
            return;
        }

        if (!_isLoaded)
        {
            _isLoaded = true;
            BuildSources();
            Choose(Sources.FirstOrDefault());
            _ = FindRosterTablesAsync();
        }
        else if (Source is { GamePath: null } && !_saving)
        {
            Choose(Source, force: true);
        }
    }

    /// <summary>Called when the mod may have changed, so an undo elsewhere doesn't leave stale cells on show.</summary>
    public void ProjectChanged()
    {
        if (!_isLoaded || _saving)
        {
            return;
        }

        BuildSources();
        bool sourceGone = Source is { GamePath: null } && _main.Project?.HasRoster != true;
        bool swapped = !ReferenceEquals(_loadedProject, _main.Project) && _loadedFromMod;
        bool changed = Source?.GamePath is { } path && _loadedFromMod != (_main.Project?.HasFile(path) == true);
        if (sourceGone)
        {
            Choose(Sources.FirstOrDefault());
        }
        else if (swapped || changed)
        {
            Choose(Sources.FirstOrDefault(s => s.GamePath == Source?.GamePath), force: true);
        }
        else if (Source is not null)
        {
            Source = Sources.FirstOrDefault(s => s.GamePath == Source.GamePath);
            MarkActive();
        }
    }

    public async Task WaitUntilReadyAsync()
    {
        await _opening;
        while (_saving)
        {
            await Task.Delay(50);
        }

        await _saveTask;
        await _changesTask;
    }

    /// <summary>The grid asks before it starts an edit, because this can stop to ask for a mod name.</summary>
    public bool CanBeginEdit() => _database is not null && _main.EnsureProject();

    internal void Commit(DatabaseRow row, DatabaseTable table, int column, string? text)
    {
        DatabaseColumn target = table.Columns[column];
        string value = text ?? string.Empty;
        if (value != table.GetCell(row.Number, target))
        {
            string? error = _main.Project is null ? "Start a mod first." : table.SetCell(row.Number, target, value);
            Problem = error;
            if (error is null)
            {
                QueueSave();
            }
            else
            {
                _main.Status = error;
            }
        }

        // After this binding has finished, or WPF keeps showing what was typed instead of what's stored.
        Dispatcher.CurrentDispatcher.BeginInvoke(row.Refresh);
    }

    /// <summary>The grid's header was clicked. Numbers sort as numbers.</summary>
    public bool SortBy(int column)
    {
        _sortDescending = _sortColumn == column && !_sortDescending;
        _sortColumn = column;
        BuildRows();
        return _sortDescending;
    }

    [RelayCommand]
    private void ChooseSource(DatabaseSourceEntry? entry) => Choose(entry);

    private void Choose(DatabaseSourceEntry? entry, bool force = false)
    {
        if (entry is not null && (force || entry.GamePath != Source?.GamePath || _database is null))
        {
            Source = entry;
            MarkActive();
            _opening = OpenAsync(entry);
        }
    }

    private void MarkActive()
    {
        foreach (DatabaseSourceEntry entry in Sources)
        {
            entry.IsActive = entry.GamePath == Source?.GamePath;
        }

        OnPropertyChanged(nameof(WhereFrom));
    }

    private void BuildSources()
    {
        var sources = new List<DatabaseSourceEntry>
        {
            new() { Label = "Main", GamePath = MainDatabase, Note = "Teams, players, arenas, uniforms and nearly everything else" },
            new() { Label = "Draft", GamePath = "db/draft_roster.db", Note = "The draft classes" },
            new() { Label = "Ratings", GamePath = "db/dynamic_ratings.db", Note = "How ratings change over a season" },
        };

        sources = sources.Where(s => _main.Index?.Find(s.GamePath!) is not null || _main.Project?.HasFile(s.GamePath!) == true).ToList();
        if (_main.Project?.HasRoster == true)
        {
            sources.Add(new DatabaseSourceEntry { Label = "My roster", GamePath = null, Note = "The roster save in your mod" });
        }

        if (sources.Select(s => s.Label).SequenceEqual(Sources.Select(s => s.Label)))
        {
            return;
        }

        Sources = sources;
        MarkActive();
    }

    private async Task OpenAsync(DatabaseSourceEntry entry)
    {
        int version = ++_openVersion;
        Message = "Reading the database…";
        Problem = null;
        string? keepTable = SelectedTable?.Table.Id;
        string keepQuery = RowQuery;
        ModProject? project = _main.Project;
        GameFileIndex? index = _main.Index;
        bool fromMod = entry.GamePath is null || project?.HasFile(entry.GamePath) == true;
        Result<GameDatabase> opened = await Task.Run(() => Read(entry.GamePath, project, index));
        if (version != _openVersion)
        {
            return;
        }

        ForgetChanges();
        _loadedProject = project;
        _loadedFromMod = fromMod;
        IsInMod = fromMod;
        if (!opened.IsSuccess)
        {
            _database = null;
            _allTables = [];
            Tables = [];
            SelectedTable = null;
            Message = opened.Error!;
            _main.Status = $"That database couldn't be read: {opened.Error}";
            return;
        }

        _database = opened.Value;
        _allTables = _database!.Tables.Select(t => new DatabaseTableEntry(t)).ToList();
        FilterTables();
        Message = string.Empty;
        SelectedTable = _allTables.FirstOrDefault(t => t.Table.Id == keepTable) ?? _allTables.FirstOrDefault(t => t.Name == "exhibitionteams") ?? _allTables.FirstOrDefault();
        if (keepQuery.Length > 0 && SelectedTable?.Table.Id == keepTable)
        {
            RowQuery = keepQuery;
        }

        _changesTask = RefreshChangesAsync();
        if (_database.ChecksumProblems.Count > 0)
        {
            _main.Status = $"This database has {_database.ChecksumProblems.Count} checksum problem(s), so it may already be damaged.";
        }
    }

    private static Result<GameDatabase> Read(string? gamePath, ModProject? project, GameFileIndex? index)
    {
        try
        {
            if (gamePath is null)
            {
                return project is null
                    ? Result.Failure<GameDatabase>("Your mod has no roster.")
                    : GameDatabase.Open(File.ReadAllBytes(project.RosterPath));
            }

            if (project?.HasFile(gamePath) == true)
            {
                Result<byte[]> own = project.ReadFile(gamePath);
                return own.IsSuccess ? GameDatabase.Open(own.Value!) : Result.Failure<GameDatabase>(own.Error!);
            }

            GameFile? file = index?.Find(gamePath);
            if (file is null)
            {
                return Result.Failure<GameDatabase>($"{gamePath} wasn't found.");
            }

            Result<byte[]> data = index!.ReadDecoded(file.Winner);
            return data.IsSuccess ? GameDatabase.Open(data.Value!) : Result.Failure<GameDatabase>(data.Error!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<GameDatabase>($"Couldn't read it: {ex.Message}", ex);
        }
    }

    /// <summary>Works out which tables a roster save overrides, for the warning on the main database.</summary>
    private async Task FindRosterTablesAsync()
    {
        string? install = _main.InstallRoot;
        GameFileIndex? index = _main.Index;
        string? modRoster = _main.Project is { HasRoster: true } project ? project.RosterPath : null;
        HashSet<string>? ids = await Task.Run(() =>
        {
            try
            {
                string? path = modRoster ?? RosterSources.Find(install, index).FirstOrDefault(s => s.SavePath is not null)?.SavePath;
                if (path is null)
                {
                    return null;
                }

                Result<GameDatabase> save = GameDatabase.Open(File.ReadAllBytes(path));
                return save.IsSuccess ? save.Value!.Tables.Select(t => t.Id).ToHashSet() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        _rosterTableIds = ids;
        OnPropertyChanged(nameof(RosterWarning));
        OnPropertyChanged(nameof(HasRosterWarning));
    }

    private void FilterTables()
    {
        string[] words = TableQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Tables = _allTables
            .Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Table.Id.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void BuildRows()
    {
        if (SelectedTable?.Table is not { } table)
        {
            Rows = [];
            return;
        }

        string query = RowQuery.Trim();
        IEnumerable<int> numbers = Enumerable.Range(0, table.RowCount);
        if (OnlyChangedRows)
        {
            numbers = numbers.Where(_changedHere.ContainsKey);
        }

        if (query.Length > 0)
        {
            numbers = numbers.Where(r => table.RowContains(r, query));
        }

        if (_sortColumn >= 0 && _sortColumn < table.Columns.Count)
        {
            DatabaseColumn column = table.Columns[_sortColumn];
            Func<int, string> cell = r => table.GetCell(r, column);
            numbers = _sortDescending
                ? numbers.OrderByDescending(cell, CellOrder.Instance)
                : numbers.OrderBy(cell, CellOrder.Instance);
        }

        Rows = numbers.Select(r => new DatabaseRow(this, table, r)).ToList();
    }

    private bool HasCurrentTable() => SelectedTable is not null;

    [RelayCommand(CanExecute = nameof(HasCurrentTable))]
    private void ExportCsv()
    {
        DatabaseTable table = SelectedTable!.Table;
        var dialog = new SaveFileDialog { Title = "Save this table as a spreadsheet", FileName = table.Name + ".csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, table.ToCsv(), System.Text.Encoding.UTF8);
            _main.Status = $"Saved {table.RowCount:N0} rows to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("The table wasn't saved", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCurrentTable))]
    private void ImportCsv()
    {
        var dialog = new OpenFileDialog { Title = "Pick a spreadsheet saved from this table", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true || !_main.EnsureProject())
        {
            return;
        }

        try
        {
            ApplyCsv(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("That file couldn't be read", ex.Message);
        }
    }

    /// <summary>Applies a whole spreadsheet to the table on show, or nothing if any cell is wrong.</summary>
    public void ApplyCsv(string csv)
    {
        if (SelectedTable?.Table is not { } table)
        {
            return;
        }

        Result<int> applied = table.ApplyCsv(csv);
        if (!applied.IsSuccess)
        {
            Problem = applied.Error;
            _main.Status = "Nothing was changed. " + applied.Error;
            return;
        }

        Problem = null;
        BuildRows();
        _main.Status = applied.Value == 0 ? "That file matches the table, so nothing changed." : $"Changed {applied.Value:N0} cells in {table.Name}.";
        if (applied.Value > 0)
        {
            QueueSave();
        }
    }

    private bool CanUndoAll() => IsInMod && Source?.GamePath is not null;

    [RelayCommand(CanExecute = nameof(CanUndoAll))]
    private async Task UndoAllAsync()
    {
        if (_main.Project is not { } project || Source?.GamePath is not { } path
            || !Dialogs.Confirm("Undo every change to this database?", $"This takes {path} out of {project.Name}, so the game's own copy applies again.", "Undo them"))
        {
            return;
        }

        await _saveTask;
        Result<bool> removed = project.RemoveFile(path);
        if (!removed.IsSuccess)
        {
            Dialogs.Tell("It wasn't undone", removed.Error!);
            return;
        }

        await _main.RefreshProjectAsync();
    }

    private void QueueSave()
    {
        if (_saving)
        {
            _saveAgain = true;
            return;
        }

        _saving = true;
        _saveTask = SaveAsync();
    }

    /// <summary>Builds the file away from the page. Edits made meanwhile trigger one more pass.</summary>
    private async Task SaveAsync()
    {
        ModProject? project = _main.Project;
        GameDatabase? database = _database;
        string? gamePath = Source?.GamePath;
        GameFileIndex? index = _main.Index;
        try
        {
            do
            {
                _saveAgain = false;
                if (project is null || database is null)
                {
                    return;
                }

                byte[] bytes = database.DatabaseBytes();
                Result<bool> written = await Task.Run(() =>
                {
                    Result<byte[]> built = database.Build(bytes);
                    if (!built.IsSuccess)
                    {
                        return Result.Failure<bool>(built.Error!);
                    }

                    if (gamePath is null)
                    {
                        return project.UpdateRoster(built.Value!);
                    }

                    Result<ProjectFile> set = project.SetFile(gamePath, built.Value!, index);
                    return set.IsSuccess ? Result.Success(true) : Result.Failure<bool>(set.Error!);
                });

                if (!written.IsSuccess)
                {
                    Problem = written.Error;
                    _main.Status = $"That wasn't saved: {written.Error}";
                    return;
                }
            }
            while (_saveAgain);

            _loadedProject = project;
            _loadedFromMod = true;
            IsInMod = true;
            _main.Status = $"Saved to {project.Name}.";
        }
        finally
        {
            _saving = false;
        }

        if (gamePath is null)
        {
            _main.Roster.FileChangedElsewhere();
        }

        _changesTask = RefreshChangesAsync();
        await _main.RefreshProjectAsync();
    }

    /// <summary>Compares two cells, as numbers when they both are.</summary>
    private sealed class CellOrder : IComparer<string>
    {
        public static readonly CellOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            bool xNumber = long.TryParse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long a);
            bool yNumber = long.TryParse(y, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long b);
            return xNumber && yNumber ? a.CompareTo(b) : StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }
    }
}
