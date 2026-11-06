using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.App.Services;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;

namespace NhlWorkbench.App.ViewModels;

public enum IceKind
{
    CenterIce,
    IceLogos,
}

public partial class IceItem : ObservableObject
{
    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isChanged;

    public required string GamePath { get; init; }

    public required string Title { get; init; }

    public required IceKind Kind { get; init; }

    public required int Id { get; init; }

    public required int Variant { get; init; }
}

public partial class IceViewModel : ObservableObject
{
    private const int ThumbnailSize = 128;
    private const int PreviewSize = 512;

    private static readonly Regex CenterLogo = new(@"^rendering/icesurface/centerlogo_(\d+)_cm\.rpsgl$", RegexOptions.Compiled);
    private static readonly Regex IceLogo = new(@"^rendering/icesurface/ice_hotspot_(\d+)_(\d+)_cm\.rpsgl$", RegexOptions.Compiled);

    private readonly MainViewModel _main;
    private readonly DdsCodec _codec = new();
    private List<IceItem> _all = [];
    private CancellationTokenSource? _loading;
    private Task _loadTask = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCenterIce), nameof(IsIceLogos))]
    private IceKind _kind = IceKind.CenterIce;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<IceItem> _visibleItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceCommand), nameof(SaveOriginalCommand), nameof(SaveDdsCommand), nameof(RevertCommand))]
    private IceItem? _selected;

    [ObservableProperty]
    private BitmapSource? _before;

    [ObservableProperty]
    private BitmapSource? _after;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    private int _centerIceCount;

    [ObservableProperty]
    private int _iceLogoCount;

    public IceViewModel(MainViewModel main) => _main = main;

    public bool IsCenterIce => Kind == IceKind.CenterIce;

    public bool IsIceLogos => Kind == IceKind.IceLogos;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private string _beforeLabel = "GAME ORIGINAL";

    partial void OnKindChanged(IceKind value) => ApplyFilter();

    partial void OnQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(IceItem? value) => _ = ShowSelectedAsync(value);

    [RelayCommand]
    private void ShowCenterIceTab() => Kind = IceKind.CenterIce;

    [RelayCommand]
    private void ShowIceLogosTab() => Kind = IceKind.IceLogos;

    public async Task StopLoadingAsync()
    {
        _loading?.Cancel();
        try
        {
            await _loadTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: that's what stopping it does.
        }
    }

    /// <summary>Finds every ice picture in the game and starts making thumbnails in the background.</summary>
    public void Load()
    {
        GameFileIndex? index = _main.Index;
        _all = [];
        if (index is not null)
        {
            foreach (GameFile file in index.Files)
            {
                Match center = CenterLogo.Match(file.Path);
                if (center.Success)
                {
                    int id = int.Parse(center.Groups[1].Value);
                    _all.Add(new IceItem { GamePath = file.Path, Title = _main.TeamCatalog?.TeamWithArt(id)?.Name ?? $"Center ice {id}", Kind = IceKind.CenterIce, Id = id, Variant = 0 });
                    continue;
                }

                Match logo = IceLogo.Match(file.Path);
                if (logo.Success)
                {
                    int variant = int.Parse(logo.Groups[1].Value);
                    int id = int.Parse(logo.Groups[2].Value);
                    _all.Add(new IceItem { GamePath = file.Path, Title = $"Ice logo {id}, style {variant}", Kind = IceKind.IceLogos, Id = id, Variant = variant });
                }
            }
        }

        // Center ice follows the team list. Numbers no team uses come after them.
        Dictionary<int, int> order = _main.TeamCatalog?.Teams
            .Select((team, at) => (team.ArtId, at))
            .GroupBy(t => t.ArtId)
            .ToDictionary(g => g.Key, g => g.Min(t => t.at)) ?? [];
        _all = _all
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Kind == IceKind.CenterIce ? order.GetValueOrDefault(i.Id, int.MaxValue) : 0)
            .ThenBy(i => i.Id)
            .ThenBy(i => i.Variant)
            .ToList();
        CenterIceCount = _all.Count(i => i.Kind == IceKind.CenterIce);
        IceLogoCount = _all.Count(i => i.Kind == IceKind.IceLogos);
        Selected = null;
        ApplyFilter();

        if (index is not null)
        {
            _loading = new CancellationTokenSource();
            CancellationToken token = _loading.Token;
            List<IceItem> items = _all;
            _loadTask = Task.Run(() => Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token },
                (item, _) =>
                {
                    item.Thumbnail = MakePicture(index, item.GamePath, ThumbnailSize, useProject: true);
                    return ValueTask.CompletedTask;
                }));
        }
    }

    public IceItem? FindItem(string gamePath) => _all.FirstOrDefault(i => i.GamePath == gamePath);

    public void RefreshChanged()
    {
        ModProject? project = _main.Project;
        var changed = project?.Files.Select(f => f.Path).ToHashSet() ?? [];
        foreach (IceItem item in _all)
        {
            item.IsChanged = changed.Contains(item.GamePath);
        }
    }

    /// <summary>Waits for the thumbnails, for callers that want everything ready.</summary>
    public async Task WaitForThumbnailsAsync()
    {
        try
        {
            await _loadTask;
        }
        catch (OperationCanceledException)
        {
            // Then there's nothing to wait for.
        }
    }

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
            await ApplyPictureAsync(dialog.FileName);
        }
    }

    /// <summary>Fits the picture to the selected texture and saves the result into the open mod.</summary>
    public async Task ApplyPictureAsync(string picturePath)
    {
        IceItem? item = Selected;
        GameFileIndex? index = _main.Index;
        if (item is null || index is null || !_main.EnsureProject())
        {
            return;
        }

        ModProject project = _main.Project!;
        _main.BusyText = "Fitting your picture";
        string note = string.Empty;
        Result<ProjectFile> saved = await Task.Run(() =>
        {
            byte[] chosen;
            try
            {
                chosen = File.ReadAllBytes(picturePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<ProjectFile>($"Can't read that picture: {ex.Message}", ex);
            }

            bool isDds = TextureDds.IsDds(chosen);
            Result<Rgba32Image>? picture = isDds ? null : _codec.DecodeImageFile(chosen);
            if (picture is { IsSuccess: false })
            {
                return Result.Failure<ProjectFile>("That file couldn't be opened as a picture.");
            }

            GameFileCopy? original = ShownCopy(index, item.GamePath);
            if (original is null)
            {
                return Result.Failure<ProjectFile>($"{item.GamePath} can't be found.");
            }

            Result<byte[]> bytes = index.ReadDecoded(original);
            if (!bytes.IsSuccess)
            {
                return Result.Failure<ProjectFile>(bytes.Error!);
            }

            Result<byte[]> swapped = picture is null
                ? TextureDds.Import(bytes.Value!, item.GamePath, chosen, _codec)
                : PictureSwap.Replace(bytes.Value!, item.GamePath, picture.Value!, _codec);
            if (!swapped.IsSuccess)
            {
                return Result.Failure<ProjectFile>(swapped.Error!);
            }

            note = string.Concat(swapped.Warnings.Select(w => w.Message + " "));
            return project.SetFile(item.GamePath, swapped.Value!, index);
        });
        _main.BusyText = null;

        if (!saved.IsSuccess)
        {
            _main.Status = "That picture wasn't added.";
            Dialogs.Tell("Picture not added", saved.Error!);
            return;
        }

        _main.Status = $"{item.Title} replaced in {project.Name}. {note}Install it from the Mod page to try it.";
        await AfterEditAsync(item);
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        IceItem item = Selected!;
        Result<bool> removed = _main.Project!.RemoveFile(item.GamePath);
        _main.Status = removed.IsSuccess ? $"{item.Title} is back to {TexturesViewModel.ShownName(_main.Index!, _main.Project, item.GamePath)}." : removed.Error!;
        await AfterEditAsync(item);
    }

    public async Task AfterEditAsync(IceItem item)
    {
        await _main.RefreshProjectAsync();
        _main.Textures.FileChanged(item.GamePath);
        GameFileIndex? index = _main.Index;
        if (index is not null)
        {
            item.Thumbnail = await Task.Run(() => MakePicture(index, item.GamePath, ThumbnailSize, useProject: true));
        }

        RevertCommand.NotifyCanExecuteChanged();
        if (Selected == item)
        {
            await ShowSelectedAsync(item);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveOriginalAsync()
    {
        IceItem item = Selected!;
        GameFileIndex? index = _main.Index;
        var dialog = new SaveFileDialog { Title = item.IsChanged ? "Save your version as PNG" : $"Save {TexturesViewModel.ShownName(_main.Index!, _main.Project, item.GamePath)} as PNG", FileName = Path.GetFileNameWithoutExtension(item.GamePath) + ".png", Filter = "PNG pictures (*.png)|*.png" };
        if (index is null || dialog.ShowDialog() != true)
        {
            return;
        }

        BitmapSource? picture = await Task.Run(() => MakePicture(index, item.GamePath, int.MaxValue, useProject: true));
        if (picture is null)
        {
            _main.Status = "That logo couldn't be read.";
            return;
        }

        Imaging.SavePng(picture, dialog.FileName);
        _main.Status = $"Saved {dialog.FileName}";
    }

    /// <summary>Saves the logo as a .dds with the game's own format and mips: your version if you've changed it, otherwise the original.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveDdsAsync()
    {
        IceItem item = Selected!;
        GameFileIndex? index = _main.Index;
        ModProject? project = _main.Project;
        bool isMine = project is not null && project.HasFile(item.GamePath);
        var dialog = new SaveFileDialog
        {
            Title = isMine ? "Save your version as DDS" : $"Save {TexturesViewModel.ShownName(_main.Index!, _main.Project, item.GamePath)} as DDS",
            FileName = Path.GetFileNameWithoutExtension(item.GamePath) + ".dds",
            Filter = "DDS textures (*.dds)|*.dds",
        };
        if (index is null || dialog.ShowDialog() != true)
        {
            return;
        }

        Result<ExportedDds> exported = await Task.Run(() =>
        {
            GameFileCopy? original = ShownCopy(index, item.GamePath);
            Result<byte[]> data = isMine
                ? project!.ReadFile(item.GamePath)
                : original is null ? Result.Failure<byte[]>($"{item.GamePath} can't be found.") : index.ReadDecoded(original);
            return data.IsSuccess
                ? TextureDds.ExportMain(data.Value!, item.GamePath, _codec)
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = "The DDS wasn't saved.";
            Dialogs.Tell("DDS not saved", ex.Message);
            return;
        }

        _main.Status = $"Saved {(isMine ? "your version" : TexturesViewModel.ShownName(_main.Index!, _main.Project, item.GamePath))} to {dialog.FileName} ({exported.Value.Format}, {exported.Value.MipCount} mips).";
    }

    private void ApplyFilter()
    {
        string query = Query.Trim();
        VisibleItems = _all
            .Where(i => i.Kind == Kind && (query.Length == 0 || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Id.ToString() == query))
            .ToList();
    }

    private async Task ShowSelectedAsync(IceItem? item)
    {
        RevertCommand.NotifyCanExecuteChanged();
        GameFileIndex? index = _main.Index;
        if (item is null || index is null)
        {
            Before = null;
            After = null;
            Details = string.Empty;
            return;
        }

        BeforeLabel = TexturesViewModel.ShownLabel(index, _main.Project, item.GamePath);
        (BitmapSource? before, BitmapSource? after, string details) = await Task.Run(() =>
        {
            GameFileCopy? original = ShownCopy(index, item.GamePath);
            string info = original is null ? string.Empty : Describe(index, original);
            return (MakePicture(index, item.GamePath, PreviewSize, useProject: false),
                    item.IsChanged ? MakePicture(index, item.GamePath, PreviewSize, useProject: true) : null,
                    info);
        });

        if (Selected == item)
        {
            Before = before;
            After = after;
            Details = details;
        }
    }

    /// <summary>What the launcher's enabled mods and the game give for this file.</summary>
    private GameFileCopy? ShownCopy(GameFileIndex index, string gamePath) =>
        index.Find(gamePath) is { } file ? TextureCatalog.ShownCopy(file, _main.Project?.Name) : null;

    private static string Describe(GameFileIndex index, GameFileCopy copy)
    {
        Result<byte[]> data = index.ReadDecoded(copy);
        Result<RpsglContainer> parsed = data.IsSuccess ? RpsglReader.Parse(data.Value!, copy.Path) : Result.Failure<RpsglContainer>(data.Error!);
        TextureEntry? main = parsed.IsSuccess ? PictureSwap.MainTexture(parsed.Value!) : null;
        return main is null ? string.Empty : $"{main.Width} × {main.Height} · {main.Format} · {main.MipCount} mips";
    }

    /// <summary>Reads and decodes one picture. Null if it can't be shown.</summary>
    private BitmapSource? MakePicture(GameFileIndex index, string gamePath, int maxSize, bool useProject)
    {
        Result<byte[]> data;
        ModProject? project = useProject ? _main.Project : null;
        if (project is not null && project.HasFile(gamePath))
        {
            data = project.ReadFile(gamePath);
        }
        else
        {
            GameFileCopy? copy = ShownCopy(index, gamePath);
            if (copy is null)
            {
                return null;
            }

            data = index.ReadDecoded(copy);
        }

        if (!data.IsSuccess)
        {
            return null;
        }

        Result<RpsglContainer> parsed = RpsglReader.Parse(data.Value!, gamePath);
        TextureEntry? main = parsed.IsSuccess ? PictureSwap.MainTexture(parsed.Value!) : null;
        if (main is null)
        {
            return null;
        }

        Result<Rgba32Image> decoded = _codec.Decode(main.LinearPixelData, main.Width, main.Height, main.Format);
        return decoded.IsSuccess ? Imaging.ToBitmap(decoded.Value!, maxSize) : null;
    }
}
