using System.IO;
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

public sealed record FileRow(GameFile File)
{
    public string Path => File.Path;

    public string Source => File.Winner.Layer.Name + (File.Winner.IsLoose ? string.Empty : $" · {File.Winner.ArchivePath}");
}

public partial class FilesViewModel : ObservableObject
{
    private const int MaxResults = 500;

    private readonly MainViewModel _main;
    private readonly DdsCodec _codec = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<FileRow> _results = [];

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(SaveCopyCommand))]
    private FileRow? _selected;

    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDdsCommand))]
    private int _textureCount;

    public FilesViewModel(MainViewModel main) => _main = main;

    public bool HasSelection => Selected is not null;

    partial void OnQueryChanged(string value) => Load();

    partial void OnSelectedChanged(FileRow? value) => _ = ShowSelectedAsync(value);

    public void Load()
    {
        GameFileIndex? index = _main.Index;
        if (index is null)
        {
            Results = [];
            ResultSummary = string.Empty;
            return;
        }

        string query = Query.Trim();
        IReadOnlyList<GameFile> found = query.Length == 0
            ? index.Files.Take(MaxResults).ToList()
            : index.Search(query, MaxResults);

        // Typing a team's name finds its files too, ahead of any whose path happens to match.
        if (query.Length > 0 && _main.TeamCatalog is { } teams)
        {
            found = teams.PathsForTeams(query)
                .Select(index.Find)
                .OfType<GameFile>()
                .Concat(found)
                .Distinct()
                .Take(MaxResults)
                .ToList();
        }

        Results = found.Select(f => new FileRow(f)).ToList();
        ResultSummary = found.Count >= MaxResults
            ? $"Showing the first {MaxResults:N0} of {index.Files.Count:N0}. Search to narrow it down."
            : $"{found.Count:N0} files";
    }

    private bool CanSave() => Selected is not null;

    /// <summary>Saves the file as the game reads it, for use in other tools.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveCopyAsync()
    {
        GameFile file = Selected!.File;
        GameFileIndex index = _main.Index!;
        var dialog = new SaveFileDialog { Title = "Save a copy", FileName = Path.GetFileName(file.Path) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Result<byte[]> data = await Task.Run(() => index.ReadDecoded(file.Winner));
        if (!data.IsSuccess)
        {
            _main.Status = data.Error!;
            return;
        }

        await File.WriteAllBytesAsync(dialog.FileName, data.Value!);
        _main.Status = $"Saved {dialog.FileName}";
    }

    private bool CanSaveDds() => TextureCount > 0;

    /// <summary>Saves the file's textures as .dds files. One texture asks for a file name; several ask for a folder.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveDds))]
    private async Task SaveDdsAsync()
    {
        GameFile file = Selected!.File;
        GameFileIndex index = _main.Index!;
        string stem = Path.GetFileNameWithoutExtension(file.Path);
        bool isOnlyTexture = TextureCount == 1;

        string destination;
        if (isOnlyTexture)
        {
            var dialog = new SaveFileDialog { Title = "Save as DDS", FileName = stem + ".dds", Filter = "DDS textures (*.dds)|*.dds" };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            destination = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = $"Pick a folder for the {TextureCount} DDS files" };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            destination = dialog.FolderName;
        }

        Result<IReadOnlyList<ExportedDds>> exported = await Task.Run(() =>
        {
            Result<byte[]> data = index.ReadDecoded(file.Winner);
            return data.IsSuccess
                ? TextureDds.ExportAll(data.Value!, file.Path, _codec)
                : Result.Failure<IReadOnlyList<ExportedDds>>(data.Error!);
        });
        if (!exported.IsSuccess)
        {
            _main.Status = "The DDS wasn't saved.";
            Dialogs.Tell("DDS not saved", exported.Error!);
            return;
        }

        var writes = exported.Value!
            .Select(d => (Path: isOnlyTexture ? destination : Path.Combine(destination, d.FileName(stem, isOnlyTexture: false)), d.Bytes))
            .ToList();
        int existing = isOnlyTexture ? 0 : writes.Count(w => File.Exists(w.Path));
        if (existing > 0 && !Dialogs.Confirm("Replace files?", $"{existing} of these {writes.Count} DDS files are already in that folder. Replace them?", "Replace"))
        {
            return;
        }

        try
        {
            foreach ((string path, byte[] bytes) in writes)
            {
                await File.WriteAllBytesAsync(path, bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = "Not all of the DDS files were saved.";
            Dialogs.Tell("DDS not saved", ex.Message);
            return;
        }

        _main.Status = isOnlyTexture ? $"Saved {destination}" : $"Saved {writes.Count} DDS files to {destination}";
    }

    private async Task ShowSelectedAsync(FileRow? row)
    {
        TextureCount = 0;
        GameFileIndex? index = _main.Index;
        if (row is null || index is null)
        {
            Preview = null;
            Details = string.Empty;
            return;
        }

        int textures = 0;
        (BitmapSource? picture, string details) = await Task.Run(() => Inspect(index, row.File, out textures));
        if (Selected == row)
        {
            Preview = picture;
            Details = details;
            TextureCount = textures;
        }
    }

    private (BitmapSource? Picture, string Details) Inspect(GameFileIndex index, GameFile file, out int textureCount)
    {
        textureCount = 0;
        var lines = new List<string> { $"Stored size: {Imaging.FormatSize(file.Winner.StoredSize)}" };
        lines.Add(file.Copies.Count == 1 ? "Used from: " + Describe(file.Winner) : "Copies, best first:");
        if (file.Copies.Count > 1)
        {
            lines.AddRange(file.Copies.Select(c => "  " + Describe(c)));
        }

        if (file.WinnerIsUncertain)
        {
            lines.Add("Which copy the game uses isn't confirmed yet.");
        }

        // Whole archives are too big to open just for a preview.
        if (index.MountedArchives.Contains(file.Path, StringComparer.Ordinal))
        {
            lines.Add("This is one of the game's main archives. Its contents are listed as separate files here.");
            return (null, string.Join('\n', lines));
        }

        Result<byte[]> data = index.ReadDecoded(file.Winner);
        if (!data.IsSuccess)
        {
            lines.Add("Can't be read: " + data.Error);
            return (null, string.Join('\n', lines));
        }

        lines.Insert(1, $"Size in use: {Imaging.FormatSize(data.Value!.Length)}");
        if (!RpsglReader.IsRpsgl(data.Value))
        {
            return (null, string.Join('\n', lines));
        }

        Result<RpsglContainer> parsed = RpsglReader.Parse(data.Value, file.Path);
        TextureEntry? main = parsed.IsSuccess ? PictureSwap.MainTexture(parsed.Value!) : null;
        if (main is null)
        {
            return (null, string.Join('\n', lines));
        }

        textureCount = parsed.Value!.Textures.Count;
        lines.Add($"Texture: {main.Width} × {main.Height} · {main.Format} · {main.MipCount} mips");
        if (parsed.Value!.Textures.Count > 1)
        {
            lines.Add($"Holds {parsed.Value.Textures.Count} textures. This shows the largest.");
        }

        Result<Rgba32Image> decoded = _codec.Decode(main.LinearPixelData, main.Width, main.Height, main.Format);
        return (decoded.IsSuccess ? Imaging.ToBitmap(decoded.Value!, 512) : null, string.Join('\n', lines));
    }

    private static string Describe(GameFileCopy copy) =>
        copy.Layer.Name + (copy.IsLoose ? ", loose file" : $", inside {copy.ArchivePath}");
}
