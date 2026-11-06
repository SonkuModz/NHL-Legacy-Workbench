using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A language as a button along the top.</summary>
public sealed partial class LanguageEntry(GameLanguage language) : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public GameLanguage Language { get; } = language;

    public string Label => Language.Name;
}

/// <summary>One string of the game's text.</summary>
public sealed partial class TextRow(string key, string original, string text) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _text = text;

    public string Key { get; } = key;

    public string Original { get; } = original;

    public bool IsChanged => Text != Original;
}

/// <summary>
/// The Text page: every string the game shows, in each language. Edits go in the mod's copy of the language file, and the
/// game's own text is kept to compare with and put back.
/// </summary>
public partial class GameTextViewModel : ObservableObject
{
    private const string Everything = "all";
    private const string TeamNames = "teams";
    private const string ChangedOnly = "changed";
    private const string KeysLanguage = "lng_lg";

    private readonly MainViewModel _main;
    private TdbLanguageFile? _file;
    private List<TextRow> _rows = [];
    private HashSet<System.Text.Rune> _knownLetters = [];
    private ListCollectionView? _view;
    private string? _loadedProject;
    private bool _isLoaded;
    private bool _showing;
    private bool _saving;
    private bool _saveAgain;
    private int _version;
    private Task _opening = Task.CompletedTask;
    private Task _saveTask = Task.CompletedTask;

    [ObservableProperty]
    private IReadOnlyList<LanguageEntry> _languages = [];

    [ObservableProperty]
    private ListCollectionView? _rowsView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRow), nameof(OriginalText))]
    [NotifyCanExecuteChangedFor(nameof(PutBackCommand))]
    private TextRow? _selected;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _filter = Everything;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFontNote))]
    private string? _fontNote;

    [ObservableProperty]
    private string _replaceFind = string.Empty;

    [ObservableProperty]
    private string _replaceWith = string.Empty;

    [ObservableProperty]
    private bool _replaceMatchCase = true;

    [ObservableProperty]
    private string _replacePreview = "Type what to look for.";

    public GameTextViewModel(MainViewModel main) => _main = main;

    public IReadOnlyList<ToolChoice> Filters { get; } =
        [new("Everything", Everything) { IsActive = true }, new("Team names", TeamNames), new("Changed", ChangedOnly)];

    public bool HasRow => Selected is not null;

    public bool HasProblem => Problem is not null;

    public bool HasFontNote => FontNote is not null;

    public string OriginalText => Selected?.Original ?? string.Empty;

    private LanguageEntry? Current => Languages.FirstOrDefault(l => l.IsActive);

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSelectedChanged(TextRow? value)
    {
        _showing = true;
        EditText = value?.Text ?? string.Empty;
        Problem = null;
        NoteLetters(EditText);
        _showing = false;
    }

    partial void OnEditTextChanged(string value)
    {
        if (!_showing)
        {
            Commit(value);
        }
    }

    public void Reset()
    {
        _isLoaded = false;
        _version++;
        _file = null;
        _rows = [];
        _view = null;
        RowsView = null;
        Selected = null;
        Languages = [];
        Summary = string.Empty;
        if (_main.IsTextPage)
        {
            EnsureLoaded();
        }
    }

    public void EnsureLoaded()
    {
        if (_isLoaded || _main.Index is not { } index)
        {
            return;
        }

        _isLoaded = true;
        Languages = GameTextSheet.Languages(index).Select(l => new LanguageEntry(l)).ToList();
        if (Languages.FirstOrDefault() is { } first)
        {
            _opening = OpenAsync(first);
        }
        else
        {
            Summary = "This game has no language files.";
        }
    }

    /// <summary>Another mod was opened, so the text on show may no longer be this one's.</summary>
    public void ProjectChanged()
    {
        if (_isLoaded && !_saving && _main.Project?.Folder != _loadedProject && Current is { } language)
        {
            _opening = OpenAsync(language);
        }
    }

    /// <summary>The mod's copy of the text may have gone or changed, so it's read again.</summary>
    public void FileChangedElsewhere()
    {
        if (_isLoaded && !_saving && Current is { } language)
        {
            _opening = OpenAsync(language);
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
    }

    /// <summary>Strings holding all these words, for quick search. Team names come first, since they're what's usually wanted.</summary>
    public IEnumerable<TextRow> FindRows(string[] words) =>
        _rows.Where(r => words.All(w => r.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.Key.StartsWith(GameTextSheet.TeamNamePrefix, StringComparison.Ordinal));

    /// <summary>Picks a string, narrowing the list down so it can be seen.</summary>
    public void ShowRow(TextRow row)
    {
        ChooseFilter(Everything);
        Query = row.Key;
        Selected = row;
    }

    public void ChooseFilter(string filter)
    {
        foreach (ToolChoice choice in Filters)
        {
            choice.IsActive = choice.Value == filter;
        }

        Filter = filter;
        Refresh();
    }

    [RelayCommand]
    private void ChooseFilterChoice(ToolChoice choice) => ChooseFilter(choice.Value!);

    [RelayCommand]
    private void ChooseLanguage(LanguageEntry? entry)
    {
        if (entry is not null && !entry.IsActive)
        {
            _opening = OpenAsync(entry);
        }
    }

    private async Task OpenAsync(LanguageEntry entry)
    {
        int version = ++_version;
        foreach (LanguageEntry language in Languages)
        {
            language.IsActive = language == entry;
        }

        GameFileIndex index = _main.Index!;
        ModProject? project = _main.Project;
        string path = entry.Language.GamePath;
        Summary = "Reading the text…";
        Selected = null;
        (Result<TdbLanguageFile> File, Dictionary<string, string>? Original) read = await Task.Run(() => Read(index, project, path));
        if (version != _version)
        {
            return;
        }

        if (!read.File.IsSuccess)
        {
            _file = null;
            _rows = [];
            RowsView = null;
            Summary = $"This language couldn't be read: {read.File.Error}";
            return;
        }

        _file = read.File.Value;
        _loadedProject = project?.Folder;
        _rows = _file!.Entries().Select(e => new TextRow(e.Key, read.Original!.GetValueOrDefault(e.Key, e.Text), e.Text)).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _knownLetters = _rows.SelectMany(r => r.Original.EnumerateRunes()).ToHashSet();
        _view = new ListCollectionView(_rows) { Filter = Matches };
        RowsView = _view;
        UpdateSummary();
    }

    /// <summary>A language as it stands: the mod's copy if it has one, otherwise the game's own.</summary>
    internal static Result<TdbLanguageFile> ReadLanguage(GameFileIndex index, ModProject? project, string path)
    {
        if (project?.HasFile(path) == true)
        {
            Result<byte[]> mine = project.ReadFile(path);
            return mine.IsSuccess ? TdbLanguageFile.Parse(mine.Value!) : Result.Failure<TdbLanguageFile>(mine.Error!);
        }

        return ReadGameLanguage(index, path);
    }

    private static Result<TdbLanguageFile> ReadGameLanguage(GameFileIndex index, string path)
    {
        GameFile? game = index.Find(path);
        GameFileCopy? copy = game is null ? null : TextureCatalog.OriginalCopy(game);
        Result<byte[]> original = copy is null ? Result.Failure<byte[]>("The game's copy can't be found.") : index.ReadDecoded(copy);
        return original.IsSuccess ? TdbLanguageFile.Parse(original.Value!) : Result.Failure<TdbLanguageFile>(original.Error!);
    }

    /// <summary>
    /// Runs a change over every real language and saves the ones it touched to the mod. A language left the same as the game's
    /// goes out of the mod, so putting something back leaves nothing behind.
    /// </summary>
    /// <param name="change">Gets a language and the game's own copy of it, and returns how many strings it changed.</param>
    public async Task<(int Strings, int Languages, string? Error)> ChangeEveryLanguageAsync(Func<TdbLanguageFile, TdbLanguageFile, int> change)
    {
        if (_main.Index is not { } index || _main.Project is not { } project)
        {
            return (0, 0, "Connect your game and open a mod first.");
        }

        EnsureLoaded();
        await WaitUntilReadyAsync();
        List<GameLanguage> languages = GameTextSheet.Languages(index).Where(l => l.Code != KeysLanguage).ToList();
        (int strings, int touched, string? error) = await Task.Run(() =>
        {
            int total = 0;
            int count = 0;
            foreach (GameLanguage language in languages)
            {
                Result<TdbLanguageFile> file = ReadLanguage(index, project, language.GamePath);
                Result<TdbLanguageFile> original = ReadGameLanguage(index, language.GamePath);
                if (!file.IsSuccess || !original.IsSuccess)
                {
                    return (total, count, $"{language.Name}: {file.Error ?? original.Error}");
                }

                int changed = change(file.Value!, original.Value!);
                if (changed == 0)
                {
                    continue;
                }

                total += changed;
                count++;
                if (NhlWorkbench.Core.Mods.TeamNames.SameText(file.Value!, original.Value!))
                {
                    Result<bool> removed = project.HasFile(language.GamePath) ? project.RemoveFile(language.GamePath) : Result.Success(true);
                    if (!removed.IsSuccess)
                    {
                        return (total, count, removed.Error);
                    }
                }
                else
                {
                    Result<ProjectFile> saved = project.SetFile(language.GamePath, file.Value!.ToBytes(), index);
                    if (!saved.IsSuccess)
                    {
                        return (total, count, saved.Error);
                    }
                }
            }

            return (total, count, (string?)null);
        });

        if (touched > 0)
        {
            FileChangedElsewhere();
            await _opening;
            await _main.RefreshProjectAsync();
        }

        return (strings, touched, error);
    }

    /// <summary>Copies the picked string's text to the same key in every language.</summary>
    [RelayCommand]
    private async Task UseEverywhereAsync()
    {
        if (Selected is not { } row || !_main.EnsureProject())
        {
            return;
        }

        if (!_main.IsWalking && !Dialogs.Confirm("Use this in every language?", $"Every language's {row.Key} becomes \"{row.Text}\", which replaces its translation there.", "Use it everywhere"))
        {
            return;
        }

        string key = row.Key;
        string text = row.Text;
        (int strings, int languages, string? error) = await ChangeEveryLanguageAsync((file, _) =>
            file.TryGetText(key, out string current) && current != text && file.TrySetText(key, text) is null ? 1 : 0);
        Problem = error;
        if (error is null)
        {
            _main.Status = strings == 0 ? "Every language already has that text." : $"Changed {key} in {languages} languages.";
            Query = key;
            Selected = _rows.FirstOrDefault(r => r.Key == key);
        }
    }

    partial void OnReplaceFindChanged(string value) => UpdateReplacePreview();

    partial void OnReplaceMatchCaseChanged(bool value) => UpdateReplacePreview();

    private void UpdateReplacePreview()
    {
        int count = _file is { } file ? GameTextSheet.CountMatches(file, ReplaceFind, ReplaceMatchCase) : 0;
        ReplacePreview = ReplaceFind.Length == 0 ? "Type what to look for." : count == 0 ? "No strings in this language hold that." : $"{count:N0} string{(count == 1 ? string.Empty : "s")} in this language.";
        ReplaceAllCommand.NotifyCanExecuteChanged();
        ReplaceEverywhereCommand.NotifyCanExecuteChanged();
    }

    private bool CanReplace() => ReplaceFind.Length > 0;

    /// <summary>Swaps text in the language on show.</summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    public void ReplaceAll()
    {
        if (_file is not { } file || !_main.EnsureProject())
        {
            return;
        }

        GameTextImport done = GameTextSheet.ReplaceAll(file, ReplaceFind, ReplaceWith, ReplaceMatchCase);
        foreach (TextRow row in _rows)
        {
            if (file.TryGetText(row.Key, out string text))
            {
                row.Text = text;
            }
        }

        Refresh();
        if (done.Changed > 0)
        {
            QueueSave();
        }

        UpdateReplacePreview();
        Problem = done.Problems.Count > 0 ? string.Join(" ", done.Problems.Take(4)) : null;
        _main.Status = $"Replaced text in {done.Changed:N0} strings. The Changed filter lists them, and each can be put back.";
    }

    /// <summary>Swaps text in every language at once.</summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    public async Task ReplaceEverywhereAsync()
    {
        if (!_main.EnsureProject())
        {
            return;
        }

        string find = ReplaceFind;
        string with = ReplaceWith;
        bool matchCase = ReplaceMatchCase;
        (int strings, int languages, string? error) = await ChangeEveryLanguageAsync((file, _) => GameTextSheet.ReplaceAll(file, find, with, matchCase).Changed);
        Problem = error;
        UpdateReplacePreview();
        if (error is null)
        {
            _main.Status = $"Replaced text in {strings:N0} strings across {languages} languages.";
        }
    }

    private static (Result<TdbLanguageFile>, Dictionary<string, string>?) Read(GameFileIndex index, ModProject? project, string path)
    {
        GameFile? game = index.Find(path);
        GameFileCopy? copy = game is null ? null : TextureCatalog.OriginalCopy(game);
        Result<byte[]> original = copy is null ? Result.Failure<byte[]>("The game's copy can't be found.") : index.ReadDecoded(copy);
        Result<TdbLanguageFile> parsedOriginal = original.IsSuccess ? TdbLanguageFile.Parse(original.Value!) : Result.Failure<TdbLanguageFile>(original.Error!);
        if (!parsedOriginal.IsSuccess)
        {
            return (parsedOriginal, null);
        }

        Dictionary<string, string> texts = [];
        foreach ((string key, string text) in parsedOriginal.Value!.Entries())
        {
            texts.TryAdd(key, text);
        }

        if (project?.HasFile(path) != true)
        {
            return (parsedOriginal, texts);
        }

        Result<byte[]> mine = project.ReadFile(path);
        return (mine.IsSuccess ? TdbLanguageFile.Parse(mine.Value!) : Result.Failure<TdbLanguageFile>(mine.Error!), texts);
    }

    private bool Matches(object item)
    {
        var row = (TextRow)item;
        if (Filter == TeamNames && !row.Key.StartsWith(GameTextSheet.TeamNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (Filter == ChangedOnly && !row.IsChanged)
        {
            return false;
        }

        return Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            row.Text.Contains(w, StringComparison.OrdinalIgnoreCase) || row.Key.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Refresh()
    {
        _view?.Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int changed = _rows.Count(r => r.IsChanged);
        int shown = _view?.Count ?? 0;
        Summary = $"{shown:N0} of {_rows.Count:N0} strings" + (changed > 0 ? $" · {changed:N0} changed" : string.Empty);
    }

    /// <summary>Changes the selected string. Also used by the checks in --shots mode.</summary>
    public void Commit(string text)
    {
        if (_file is not { } file || Selected is not { } row || text == row.Text)
        {
            return;
        }

        if (!_main.EnsureProject())
        {
            return;
        }

        if (file.TrySetText(row.Key, text) is { } error)
        {
            Problem = error;
            return;
        }

        Problem = null;
        NoteLetters(text);
        row.Text = text;
        UpdateSummary();
        PutBackCommand.NotifyCanExecuteChanged();
        QueueSave();
    }

    /// <summary>
    /// The file can store any letter, but the game can only draw what its fonts have. A letter this language never uses
    /// may well not be one of them, and only the game can say for sure.
    /// </summary>
    private void NoteLetters(string text)
    {
        List<string> unknown = text.EnumerateRunes().Where(r => !_knownLetters.Contains(r)).Select(r => r.ToString()).Distinct().ToList();
        FontNote = unknown.Count == 0 ? null : $"The game's own {Current?.Label} text never uses {string.Join(" ", unknown)}, so its fonts may not have {(unknown.Count == 1 ? "it" : "them")}. Check this in the game.";
    }

    private bool CanPutBack() => Selected?.IsChanged == true;

    [RelayCommand(CanExecute = nameof(CanPutBack))]
    private void PutBack()
    {
        if (Selected is { } row)
        {
            EditText = row.Original;
        }
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

    private async Task SaveAsync()
    {
        ModProject? project = _main.Project;
        GameFileIndex? index = _main.Index;
        LanguageEntry? language = Current;
        try
        {
            do
            {
                _saveAgain = false;
                if (project is null || index is null || language is null || _file is null)
                {
                    return;
                }

                // With nothing left changed the mod shouldn't carry the file at all.
                bool anyChanged = _rows.Any(r => r.IsChanged);
                byte[] bytes = _file.ToBytes();
                string path = language.Language.GamePath;
                string? error = await Task.Run(() =>
                {
                    if (anyChanged)
                    {
                        Result<ProjectFile> saved = project.SetFile(path, bytes, index);
                        return saved.IsSuccess ? null : saved.Error;
                    }

                    Result<bool> removed = project.HasFile(path) ? project.RemoveFile(path) : Result.Success(true);
                    return removed.IsSuccess ? null : removed.Error;
                });
                if (error is not null)
                {
                    Problem = error;
                    _main.Status = $"The text wasn't saved: {error}";
                    return;
                }
            }
            while (_saveAgain);

            _main.Status = $"Saved the {language!.Label} text to {project!.Name}.";
        }
        finally
        {
            _saving = false;
        }

        _loadedProject = project.Folder;
        await _main.RefreshProjectAsync();
    }

    [RelayCommand]
    private async Task ExportCsvAsync(string which)
    {
        if (_file is not { } file || Current is not { } language)
        {
            return;
        }

        bool onlyChanged = which == "changed";
        var dialog = new SaveFileDialog { Title = "Save the text as a CSV", FileName = $"text_{language.Language.Code}.csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ISet<string>? keys = onlyChanged ? _rows.Where(r => r.IsChanged).Select(r => r.Key).ToHashSet() : null;
            await File.WriteAllTextAsync(dialog.FileName, GameTextSheet.ToCsv(file, keys), new UTF8Encoding(true));
            _main.Status = $"Saved {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("The CSV wasn't saved", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var dialog = new OpenFileDialog { Title = "Choose a text CSV", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ApplyCsv(await File.ReadAllTextAsync(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Tell("That file couldn't be read", ex.Message);
        }
    }

    /// <summary>Applies a CSV to the language on show, keeping the good lines and listing the bad ones.</summary>
    public void ApplyCsv(string csv)
    {
        if (_file is not { } file || !_main.EnsureProject())
        {
            return;
        }

        Result<GameTextImport> applied = GameTextSheet.Apply(file, csv);
        if (!applied.IsSuccess)
        {
            Dialogs.Tell("Nothing was changed", applied.Error!);
            return;
        }

        foreach (TextRow row in _rows)
        {
            if (file.TryGetText(row.Key, out string text))
            {
                row.Text = text;
            }
        }

        Refresh();
        if (applied.Value!.Changed > 0)
        {
            QueueSave();
        }

        if (applied.Value.Problems.Count > 0)
        {
            Dialogs.Tell($"{applied.Value.Problems.Count} line{(applied.Value.Problems.Count == 1 ? " wasn't" : "s weren't")} used", string.Join(Environment.NewLine, applied.Value.Problems.Take(12)));
        }

        _main.Status = $"The CSV changed {applied.Value.Changed:N0} strings.";
    }
}
