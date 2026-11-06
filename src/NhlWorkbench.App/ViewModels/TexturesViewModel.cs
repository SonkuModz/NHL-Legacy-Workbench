using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

public partial class TextureItem : ObservableObject
{
    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isChanged;

    [ObservableProperty]
    private bool _isSelected;

    public required TextureFile File { get; init; }

    public string Title => Path.GetFileNameWithoutExtension(File.Path);

    public string Folder => File.Path.Split('/') is { Length: >= 3 } parts ? parts[^2] : string.Empty;

    /// <summary>"Toronto Maple Leafs · Kit 2 · Jersey", when the file belongs to a team.</summary>
    public string? Owner { get; set; }

    /// <summary>Warns when changing this file changes more than one team.</summary>
    public string? SharedNote { get; set; }

    public bool HasOwner => Owner is not null;

    public bool IsShared => SharedNote is not null;

    public string Subtitle => Owner ?? Folder;

    public bool HasSeveral => File.Textures.Count > 1;

    public string CountText => $"{File.Textures.Count} TEX";

    /// <summary>True while its tile is on screen.</summary>
    public volatile bool IsWanted;

    /// <summary>True while it's waiting for a thumbnail. Guarded by the queue's lock.</summary>
    public bool IsQueued;
}

/// <summary>One line of tiles. The list virtualizes whole rows, since WPF has no virtualizing wrap panel.</summary>
public sealed record TextureRow(IReadOnlyList<TextureItem> Items);

public partial class TextureCategory : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public required string Name { get; init; }

    /// <summary>Null means every category.</summary>
    public string? Filter { get; init; }

    public required int Count { get; init; }

    public string Label => $"{Name}  {Count:N0}";
}

public partial class TextureChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public required TextureInfo Info { get; init; }

    public string? Role { get; init; }

    public string Label => Role ?? $"{Info.Index + 1}";

    public string Hint => $"{Info.Width} × {Info.Height} · {Info.Format}";
}

public partial class TexturesViewModel : ObservableObject
{
    private const int ThumbnailSize = 128;
    private const int PreviewSize = 512;
    private const int MaxCachedThumbnails = 1500;
    private const int LotsToSave = 500;

    private readonly MainViewModel _main;
    private readonly DdsCodec _codec = new();
    private readonly Stack<TextureItem> _queue = new();
    private readonly Queue<TextureItem> _cached = new();
    private readonly SemaphoreSlim _queued = new(0);
    private Dictionary<string, TextureItem> _byPath = [];
    private List<TextureItem> _all = [];
    private List<TextureItem> _filtered = [];
    private CancellationTokenSource? _work;
    private Task _catalogTask = Task.CompletedTask;

    [ObservableProperty]
    private string _beforeLabel = "GAME ORIGINAL";
    private List<Task> _workers = [];
    private bool _isLoaded;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<TextureCategory> _categories = [];

    [ObservableProperty]
    private IReadOnlyList<TextureRow> _rows = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasSeveralChoices))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(SavePngCommand), nameof(SaveDdsCommand), nameof(RevertCommand))]
    private TextureItem? _selected;

    [ObservableProperty]
    private IReadOnlyList<TextureChoice> _choices = [];

    [ObservableProperty]
    private TextureChoice? _choice;

    [ObservableProperty]
    private BitmapSource? _before;

    [ObservableProperty]
    private BitmapSource? _after;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlphaButtonText))]
    private bool _ignoreTransparency;

    public TexturesViewModel(MainViewModel main) => _main = main;

    public bool HasSelection => Selected is not null;

    /// <summary>True once the list of textures has been read.</summary>
    public bool HasCatalog => _all.Count > 0;

    public TextureItem? FindItem(string gamePath) => _byPath.GetValueOrDefault(gamePath);

    /// <summary>Every texture file in the game.</summary>
    public List<TextureFile> AllFiles() => _all.Select(i => i.File).ToList();

    /// <summary>How many pictures are showing, which is what Save these would save.</summary>
    public int ShownCount => _filtered.Count;

    [RelayCommand]
    private async Task SaveShownAsync(string format)
    {
        if (_filtered.Count == 0)
        {
            return;
        }

        if (_filtered.Count > LotsToSave
            && !Dialogs.Confirm("Save all these?", $"That's {_filtered.Count:N0} files, which can take a while and use a lot of disk space. Searching or picking a category first narrows it down.", "Save them all"))
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose where to put the pictures' folder" };
        if (dialog.ShowDialog() == true)
        {
            await SaveShownToAsync(dialog.FolderName, format == "png" ? TeamBundleFormat.Png : TeamBundleFormat.Dds);
        }
    }

    /// <summary>Saves what's showing as a folder that can be brought back in. Returns the folder, or null if it failed.</summary>
    public Task<string?> SaveShownToAsync(string parent, TeamBundleFormat format)
    {
        string category = Categories.FirstOrDefault(c => c.IsActive)?.Filter ?? "All";
        string name = Query.Trim().Length > 0 ? $"Textures - {category} - {Query.Trim()}" : $"Textures - {category}";
        return _main.Teams.ExportArtToAsync(name, -1, _filtered.Select(i => new TeamArt(i.File.Path, TeamSection.Logos, i.Folder, i.Title)).ToList(), parent, format, sortBySection: false);
    }

    /// <summary>Pictures whose path or team holds all these words, for quick search.</summary>
    public IEnumerable<TextureItem> FindItems(string[] words) =>
        _all.Where(i => words.All(w => i.File.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Owner?.Contains(w, StringComparison.OrdinalIgnoreCase) == true));

    public bool HasSeveralChoices => Choices.Count > 1;

    public string AlphaButtonText => IgnoreTransparency ? "SHOW TRANSPARENCY" : "HIDE TRANSPARENCY";

    public int Columns
    {
        get => field;
        set
        {
            if (value != field && value > 0)
            {
                field = value;
                BuildRows();
            }
        }
    } = 4;

    partial void OnQueryChanged(string value) => ApplyFilter();

    partial void OnChoicesChanged(IReadOnlyList<TextureChoice> value) => OnPropertyChanged(nameof(HasSeveralChoices));

    partial void OnIgnoreTransparencyChanged(bool value) => _ = ShowSelectedAsync();

    partial void OnSelectedChanged(TextureItem? oldValue, TextureItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        List<TextureChoice> choices = newValue?.File.Textures.Select(t => new TextureChoice { Info = t, Role = TextureCatalog.RoleFor(newValue.File.Path, t.Index, newValue.File.Textures.Count) }).ToList() ?? [];
        Choices = choices;
        SetChoice(newValue is null ? null : choices.First(c => c.Info.Index == newValue.File.Main.Index));
    }

    /// <summary>Stops all background work, ready for the game to change.</summary>
    public async Task StopAsync()
    {
        _work?.Cancel();
        try
        {
            await Task.WhenAll([_catalogTask, .. _workers]);
        }
        catch (OperationCanceledException)
        {
            // Expected: that's what stopping it does.
        }

        lock (_queue)
        {
            _queue.Clear();
        }

        _workers = [];
    }

    /// <summary>Forgets the old game. The new one is read the next time the page opens.</summary>
    public void Reset()
    {
        _isLoaded = false;
        _all = [];
        _byPath = [];
        _cached.Clear();
        Selected = null;
        Categories = [];
        Summary = string.Empty;
        ApplyFilter();
        if (_main.CurrentPage == this)
        {
            EnsureLoaded();
        }
    }

    /// <summary>Reads the list of textures the first time it's needed, since most sessions may not open this page.</summary>
    public void EnsureLoaded()
    {
        GameFileIndex? index = _main.Index;
        if (_isLoaded || index is null)
        {
            return;
        }

        _isLoaded = true;
        _work = new CancellationTokenSource();
        CancellationToken cancel = _work.Token;
        Summary = "Reading the textures…";
        _catalogTask = LoadCatalogAsync(index, cancel);
    }

    /// <summary>Waits for the list, for callers that want everything ready.</summary>
    public async Task WaitForCatalogAsync()
    {
        try
        {
            await _catalogTask;
        }
        catch (OperationCanceledException)
        {
            // Then there's nothing to wait for.
        }
    }

    public void RefreshChanged()
    {
        var changed = _main.Project?.Files.Select(f => f.Path).ToHashSet() ?? [];
        foreach (TextureItem item in _all)
        {
            item.IsChanged = changed.Contains(item.File.Path);
        }

        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called when a file's contents changed, on this page or another.</summary>
    public void FileChanged(string gamePath)
    {
        if (!_byPath.TryGetValue(gamePath, out TextureItem? item))
        {
            return;
        }

        item.Thumbnail = null;
        if (item.IsWanted)
        {
            Need(item);
        }

        if (Selected == item)
        {
            _ = ShowSelectedAsync();
        }
    }

    /// <summary>A tile came on screen.</summary>
    public void Need(TextureItem item)
    {
        item.IsWanted = true;
        if (item.Thumbnail is not null)
        {
            return;
        }

        lock (_queue)
        {
            if (item.IsQueued)
            {
                return;
            }

            item.IsQueued = true;
            _queue.Push(item);
        }

        _queued.Release();
    }

    /// <summary>A tile went off screen. Anything still waiting for it is skipped.</summary>
    public void NotNeeded(TextureItem item) => item.IsWanted = false;

    public async Task ApplyFileAsync(string chosenPath)
    {
        TextureItem? item = Selected;
        TextureInfo? texture = Choice?.Info;
        GameFileIndex? index = _main.Index;
        if (item is null || texture is null || index is null || !_main.EnsureProject())
        {
            return;
        }

        ModProject project = _main.Project!;
        string gamePath = item.File.Path;
        _main.BusyText = "Fitting your picture";
        string note = string.Empty;
        Result<ProjectFile> saved = await Task.Run(() =>
        {
            byte[] chosen;
            try
            {
                chosen = File.ReadAllBytes(chosenPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<ProjectFile>($"Can't read that file: {ex.Message}", ex);
            }

            Result<Rgba32Image>? picture = TextureDds.IsDds(chosen) ? null : _codec.DecodeImageFile(chosen);
            if (picture is { IsSuccess: false })
            {
                return Result.Failure<ProjectFile>("That file couldn't be opened as a picture.");
            }

            // Starts from your version, so earlier edits to other textures in this file are kept.
            Result<byte[]> current = ReadFile(index, project, gamePath);
            if (!current.IsSuccess)
            {
                return Result.Failure<ProjectFile>(current.Error!);
            }

            Result<byte[]> swapped = picture is null
                ? GameTextures.Import(current.Value!, gamePath, chosen, _codec, texture.Index)
                : GameTextures.Replace(current.Value!, gamePath, picture.Value!, _codec, texture.Index);
            if (!swapped.IsSuccess)
            {
                return Result.Failure<ProjectFile>(swapped.Error!);
            }

            note = string.Concat(swapped.Warnings.Select(w => w.Message + " "));
            return project.SetFile(gamePath, swapped.Value!, index);
        });
        _main.BusyText = null;

        if (!saved.IsSuccess)
        {
            _main.Status = "That file wasn't added.";
            Dialogs.Tell("Not added", saved.Error!);
            return;
        }

        _main.Status = $"{item.Title} replaced in {project.Name}. {note}Install it from the Mod page to try it.";
        await AfterEditAsync(gamePath);
    }

    [RelayCommand]
    private void ShowCategory(TextureCategory? category)
    {
        if (category is null)
        {
            return;
        }

        foreach (TextureCategory other in Categories)
        {
            other.IsActive = other == category;
        }

        ApplyFilter();
    }

    [RelayCommand]
    private void SelectItem(TextureItem? item) => Selected = item;

    [RelayCommand]
    private void ChooseTexture(TextureChoice? choice)
    {
        if (choice is not null)
        {
            SetChoice(choice);
        }
    }

    [RelayCommand]
    private void ToggleTransparency() => IgnoreTransparency = !IgnoreTransparency;

    private bool CanEdit() => Selected is not null;

    private bool CanRevert() => Selected?.IsChanged == true;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ReplaceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Pick a picture or DDS for {Selected!.Title}",
            Filter = "Pictures and DDS files (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.dds",
        };
        if (dialog.ShowDialog() == true)
        {
            await ApplyFileAsync(dialog.FileName);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        TextureItem item = Selected!;
        if (item.HasSeveral && !Dialogs.Confirm("Undo this whole file?", $"{item.Title} holds {item.File.Textures.Count} textures. This takes all of them out of your mod.", "Undo all"))
        {
            return;
        }

        Result<bool> removed = _main.Project!.RemoveFile(item.File.Path);
        _main.Status = removed.IsSuccess ? $"{item.Title} is back to {ShownName(_main.Index!, _main.Project, item.File.Path)}." : removed.Error!;
        await AfterEditAsync(item.File.Path);
    }

    /// <summary>Saves the chosen texture at full size: your version if you've changed the file, otherwise the original.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SavePngAsync()
    {
        TextureItem item = Selected!;
        TextureInfo texture = Choice!.Info;
        GameFileIndex index = _main.Index!;
        ModProject? project = _main.Project;
        bool isMine = project?.HasFile(item.File.Path) == true;
        var dialog = new SaveFileDialog
        {
            Title = isMine ? "Save your version as PNG" : $"Save {ShownName(index, project, item.File.Path)} as PNG",
            FileName = SuggestedName(item, texture) + ".png",
            Filter = "PNG pictures (*.png)|*.png",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        BitmapSource? picture = await Task.Run(() => MakePicture(index, project, item.File.Path, texture.Index, int.MaxValue, IgnoreTransparency));
        if (picture is null)
        {
            _main.Status = "That texture couldn't be read.";
            return;
        }

        try
        {
            Imaging.SavePng(picture, dialog.FileName);
            _main.Status = $"Saved {(isMine ? "your version" : ShownName(index, project, item.File.Path))} to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = "The PNG wasn't saved.";
            Dialogs.Tell("PNG not saved", ex.Message);
        }
    }

    /// <summary>Saves the chosen texture with the game's own format and mips: your version if you've changed the file, otherwise the original.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveDdsAsync()
    {
        TextureItem item = Selected!;
        TextureInfo texture = Choice!.Info;
        GameFileIndex index = _main.Index!;
        ModProject? project = _main.Project;
        bool isMine = project?.HasFile(item.File.Path) == true;
        var dialog = new SaveFileDialog
        {
            Title = isMine ? "Save your version as DDS" : $"Save {ShownName(index, project, item.File.Path)} as DDS",
            FileName = SuggestedName(item, texture) + ".dds",
            Filter = "DDS textures (*.dds)|*.dds",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Result<ExportedDds> exported = await Task.Run(() =>
        {
            Result<byte[]> data = ReadFile(index, project, item.File.Path);
            return data.IsSuccess
                ? GameTextures.Export(data.Value!, item.File.Path, _codec, texture.Index)
                : Result.Failure<ExportedDds>(data.Error!);
        });
        if (!exported.IsSuccess)
        {
            _main.Status = "The DDS wasn't saved.";
            Dialogs.Tell("DDS not saved", exported.Error!);
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, exported.Value!.Bytes);
            _main.Status = $"Saved {(isMine ? "your version" : ShownName(index, project, item.File.Path))} to {dialog.FileName} ({exported.Value.Format}, {exported.Value.MipCount} mips).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = "The DDS wasn't saved.";
            Dialogs.Tell("DDS not saved", ex.Message);
        }
    }

    private static string SuggestedName(TextureItem item, TextureInfo texture) =>
        item.HasSeveral ? $"{item.Title}_{texture.Index + 1}" : item.Title;

    private async Task LoadCatalogAsync(GameFileIndex index, CancellationToken cancel)
    {
        TextureCatalog catalog = await Task.Run(() => TextureCatalog.Build(index, cancel, done => Summary = $"Reading the textures… {done:N0} files"), cancel);

        _all = catalog.Files.Select(f => new TextureItem { File = f }).ToList();
        foreach (TextureItem item in _all)
        {
            DescribeOwner(item);
        }

        _byPath = _all.ToDictionary(i => i.File.Path, StringComparer.Ordinal);
        var counts = catalog.Files.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count());

        var categories = new List<TextureCategory> { new() { Name = "All", Count = _all.Count, IsActive = true } };
        categories.AddRange(TextureCatalog.Categories
            .Where(counts.ContainsKey)
            .Select(c => new TextureCategory { Name = c, Filter = c, Count = counts[c] }));
        Categories = categories;

        RefreshChanged();
        ApplyFilter();

        int count = Math.Max(2, Environment.ProcessorCount / 2);
        _workers = Enumerable.Range(0, count).Select(_ => Task.Run(() => MakeThumbnailsAsync(index, cancel), cancel)).ToList();
    }

    private async Task MakeThumbnailsAsync(GameFileIndex index, CancellationToken cancel)
    {
        while (true)
        {
            try
            {
                await _queued.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TextureItem? item;
            lock (_queue)
            {
                if (!_queue.TryPop(out item))
                {
                    continue;
                }

                item.IsQueued = false;
            }

            if (!item.IsWanted || item.Thumbnail is not null)
            {
                continue;
            }

            BitmapSource? thumbnail = MakePicture(index, _main.Project, item.File.Path, item.File.Main.Index, ThumbnailSize, ignoreTransparency: false);
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            item.Thumbnail = thumbnail;
            Remember(item);
        }
    }

    /// <summary>Keeps recent thumbnails so scrolling back is instant, without holding all of them in memory.</summary>
    private void Remember(TextureItem item)
    {
        lock (_cached)
        {
            _cached.Enqueue(item);
            int checks = _cached.Count;
            while (_cached.Count > MaxCachedThumbnails && checks-- > 0)
            {
                TextureItem oldest = _cached.Dequeue();
                if (oldest.IsWanted)
                {
                    _cached.Enqueue(oldest);
                }
                else
                {
                    oldest.Thumbnail = null;
                }
            }
        }
    }

    private void ApplyFilter()
    {
        string? category = Categories.FirstOrDefault(c => c.IsActive)?.Filter;
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _filtered = _all
            .Where(i => category is null || i.File.Category == category)
            .Where(i => words.All(w => i.File.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Owner?.Contains(w, StringComparison.OrdinalIgnoreCase) == true))
            .ToList();

        if (_isLoaded && _all.Count > 0)
        {
            Summary = _filtered.Count == 1 ? "1 file" : $"{_filtered.Count:N0} files";
        }

        BuildRows();
    }

    private void DescribeOwner(TextureItem item)
    {
        if (_main.TeamCatalog is not { } teams)
        {
            return;
        }

        IReadOnlyList<TeamArtOwner> owners = teams.OwnersOf(item.File.Path);
        if (owners.Count == 0)
        {
            return;
        }

        TeamArtOwner first = owners[0];
        item.Owner = first.Art.Section == TeamSection.Uniforms
            ? $"{first.Team.Name} · {first.Art.Group} · {first.Art.Label}"
            : $"{first.Team.Name} · {first.Art.Label}";
        if (owners.Count > 1)
        {
            item.Owner = $"{first.Team.Name} and {owners.Count - 1} more · {first.Art.Label}";
            item.SharedNote = $"{owners.Count} teams use this file, so changing it changes all of them.";
        }
    }

    private void BuildRows()
    {
        Rows = _filtered.Chunk(Columns).Select(chunk => new TextureRow(chunk)).ToList();
    }

    private void SetChoice(TextureChoice? choice)
    {
        foreach (TextureChoice other in Choices)
        {
            other.IsActive = other == choice;
        }

        Choice = choice;
        _ = ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        TextureItem? item = Selected;
        TextureChoice? choice = Choice;
        GameFileIndex? index = _main.Index;
        RevertCommand.NotifyCanExecuteChanged();
        if (item is null || choice is null || index is null)
        {
            Before = null;
            After = null;
            Details = string.Empty;
            return;
        }

        TextureInfo texture = choice.Info;
        Details = $"{texture.Width} × {texture.Height} · {texture.Format} · {texture.MipCount} mips";
        ModProject? project = _main.Project;
        bool ignore = IgnoreTransparency;
        bool changed = item.IsChanged;
        BeforeLabel = ShownLabel(index, project, item.File.Path);
        (BitmapSource? before, BitmapSource? after) = await Task.Run(() => (
            MakePicture(index, project, item.File.Path, texture.Index, PreviewSize, ignore, useMine: false),
            changed ? MakePicture(index, project, item.File.Path, texture.Index, PreviewSize, ignore) : null));

        if (Selected == item && Choice == choice && IgnoreTransparency == ignore)
        {
            Before = before;
            After = after;
        }
    }

    /// <summary>Your version of the file if there is one, otherwise what the launcher's enabled mods and the game give.</summary>
    internal static Result<byte[]> ReadFile(GameFileIndex index, ModProject? project, string gamePath, bool useMine = true)
    {
        if (useMine && project?.HasFile(gamePath) == true)
        {
            return project.ReadFile(gamePath);
        }

        GameFile? file = index.Find(gamePath);
        GameFileCopy? copy = file is null ? null : TextureCatalog.ShownCopy(file, project?.Name);
        return copy is null ? Result.Failure<byte[]>($"{gamePath} can't be found.") : index.ReadDecoded(copy);
    }

    /// <summary>The enabled mod a file comes from, or null if it's the game's own.</summary>
    private static string? ShownMod(GameFileIndex index, ModProject? project, string gamePath)
    {
        GameFile? file = index.Find(gamePath);
        return file is not null && TextureCatalog.ShownCopy(file, project?.Name) is { Layer.Kind: GameLayerKind.Mod } copy ? copy.Layer.Name : null;
    }

    /// <summary>"The game's original", or the enabled mod it comes from, for messages.</summary>
    internal static string ShownName(GameFileIndex index, ModProject? project, string gamePath) =>
        ShownMod(index, project, gamePath) is { } mod ? $"{mod}'s version" : "the game's original";

    /// <summary>The heading over the picture that isn't yours.</summary>
    internal static string ShownLabel(GameFileIndex index, ModProject? project, string gamePath) =>
        ShownMod(index, project, gamePath) is { } mod ? $"FROM {mod.ToUpperInvariant()}" : "GAME ORIGINAL";

    private BitmapSource? MakePicture(GameFileIndex index, ModProject? project, string gamePath, int textureIndex, int maxSize, bool ignoreTransparency, bool useMine = true)
    {
        Result<byte[]> data = ReadFile(index, project, gamePath, useMine);
        Result<GameTexture> read = data.IsSuccess ? GameTextures.Read(data.Value!, gamePath, textureIndex, _codec) : Result.Failure<GameTexture>(data.Error!);
        if (!read.IsSuccess)
        {
            return null;
        }

        GameTexture texture = read.Value!;
        Result<Rgba32Image> decoded = maxSize == int.MaxValue
            ? _codec.Decode(texture.LinearPixelData, texture.Width, texture.Height, texture.Format)
            : _codec.DecodeForPreview(texture.LinearPixelData, texture.Width, texture.Height, texture.Format, texture.MipCount, maxSize);
        if (!decoded.IsSuccess)
        {
            return null;
        }

        byte[] pixels = decoded.Value!.Pixels;
        if (ignoreTransparency || IsFullyTransparent(pixels))
        {
            // Some textures use alpha for something else, which would otherwise show as an empty square.
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return Imaging.ToBitmap(decoded.Value!, maxSize);
    }

    private static bool IsFullyTransparent(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private async Task AfterEditAsync(string gamePath)
    {
        await _main.RefreshProjectAsync();
        FileChanged(gamePath);
        IceItem? ice = _main.Ice.FindItem(gamePath);
        if (ice is not null)
        {
            await _main.Ice.AfterEditAsync(ice);
        }
    }
}
