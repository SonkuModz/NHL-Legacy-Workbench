using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Videos;

namespace NhlWorkbench.App.ViewModels;

/// <summary>A movie as a tile in the grid.</summary>
public partial class VideoEntry(GameVideo video) : ObservableObject
{
    [ObservableProperty]
    private BitmapImage? _thumbnail;

    public GameVideo Video { get; } = video;

    public string Title => Video.Name;

    public string Length => Video.Seconds >= 60
        ? $"{(int)Video.Seconds / 60}:{(int)Video.Seconds % 60:00}"
        : Video.Seconds.ToString("0.0 s", CultureInfo.InvariantCulture);
}

/// <summary>
/// The Videos page: every movie in the game, to look through, watch and save as an MP4. They're read-only, since neither
/// mods nor the tools at hand can make new ones.
/// </summary>
public partial class VideosViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _cacheFolder = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Videos");
    private bool _isLoaded;
    private int _version;
    private Task _thumbnails = Task.CompletedTask;
    private Task _preparing = Task.CompletedTask;

    [ObservableProperty]
    private IReadOnlyList<VideoEntry> _videos = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideo), nameof(Facts))]
    [NotifyCanExecuteChangedFor(nameof(SaveMp4Command), nameof(SaveFrameCommand))]
    private VideoEntry? _selected;

    /// <summary>The MP4 the player on the page should play.</summary>
    [ObservableProperty]
    private string? _playFile;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public VideosViewModel(MainViewModel main) => _main = main;

    public bool HasVideo => Selected is not null;

    public bool HasProblem => Problem is not null;

    public string Facts => Selected?.Video is { } video
        ? $"{video.Width}×{video.Height} · {video.FramesPerSecond:0.##} fps · {Selected.Length} · {NhlWorkbench.App.Services.Imaging.FormatSize(video.Size)} · from {video.Archive}.big"
        : string.Empty;

    partial void OnSelectedChanged(VideoEntry? value)
    {
        Problem = null;
        PlayFile = null;
        _preparing = PrepareAsync(value);
    }

    public void Reset()
    {
        _version++;
        _isLoaded = false;
        Videos = [];
        Selected = null;
        Message = string.Empty;
        if (_main.IsVideosPage)
        {
            EnsureLoaded();
        }
    }

    public void EnsureLoaded()
    {
        if (_isLoaded || _main.Index is null)
        {
            return;
        }

        _isLoaded = true;
        if (_main.InstallRoot is not { } install)
        {
            Message = "The movies are made when the game is installed, so connect your game folder rather than a disc image.";
            return;
        }

        int version = ++_version;
        IReadOnlyList<GameVideo> found = GameVideos.Find(install);
        Videos = found.Select(v => new VideoEntry(v)).ToList();
        Message = Videos.Count == 0 ? "No movies were found. The launcher makes them the first time it sets up the game." : string.Empty;
        _thumbnails = MakeThumbnailsAsync(Videos, version);
    }

    public async Task WaitUntilReadyAsync()
    {
        await _thumbnails;
        await _preparing;
    }

    private async Task MakeThumbnailsAsync(IReadOnlyList<VideoEntry> entries, int version)
    {
        if (_main.FfmpegPath is not { } ffmpeg)
        {
            return;
        }

        foreach (VideoEntry entry in entries)
        {
            if (version != _version)
            {
                return;
            }

            GameVideo video = entry.Video;
            string png = CachePath(video, ".png");
            BitmapImage? image = await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(_cacheFolder);
                    double when = Math.Min(2, video.Seconds / 2);
                    if (!File.Exists(png) && !GameVideos.SaveFrame(video, when, 320, png, ffmpeg).IsSuccess)
                    {
                        return null;
                    }

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(png);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
                {
                    return null;
                }
            });
            entry.Thumbnail = image;
        }
    }

    /// <summary>The player can't read VP6, so a movie is turned into an MP4 the first time it's opened.</summary>
    private async Task PrepareAsync(VideoEntry? entry)
    {
        if (entry is null || _main.FfmpegPath is not { } ffmpeg)
        {
            return;
        }

        string mp4 = CachePath(entry.Video, ".mp4");
        Result<bool> made = File.Exists(mp4) ? Result.Success(true) : await Task.Run(() => MakeMp4(entry.Video, mp4, ffmpeg));
        if (Selected != entry)
        {
            return;
        }

        if (made.IsSuccess)
        {
            PlayFile = mp4;
        }
        else
        {
            Problem = $"This movie couldn't be opened: {made.Error}";
        }
    }

    /// <summary>Written under another name first, so a half-made file is never taken for a finished one.</summary>
    private Result<bool> MakeMp4(GameVideo video, string mp4, string ffmpeg)
    {
        try
        {
            Directory.CreateDirectory(_cacheFolder);
            string partial = mp4 + ".part.mp4";
            Result<bool> made = GameVideos.SaveMp4(video, partial, ffmpeg);
            if (made.IsSuccess)
            {
                File.Move(partial, mp4, overwrite: true);
            }

            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<bool>(ex.Message, ex);
        }
    }

    private string CachePath(GameVideo video, string extension) =>
        Path.Combine(_cacheFolder, $"{video.Archive}_{video.Offset}_{video.Size.ToString(CultureInfo.InvariantCulture)}{extension}");

    private bool CanSave() => Selected is not null && _main.FfmpegPath is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveMp4Async()
    {
        VideoEntry entry = Selected!;
        var dialog = new SaveFileDialog { Title = "Save this movie", FileName = entry.Title.Replace(' ', '_') + ".mp4", Filter = "MP4 files (*.mp4)|*.mp4" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _main.BusyText = "Saving the movie";
        string ffmpeg = _main.FfmpegPath!;
        string mp4 = CachePath(entry.Video, ".mp4");
        Result<bool> saved = await Task.Run(() =>
        {
            try
            {
                Result<bool> made = File.Exists(mp4) ? Result.Success(true) : MakeMp4(entry.Video, mp4, ffmpeg);
                if (made.IsSuccess)
                {
                    File.Copy(mp4, dialog.FileName, overwrite: true);
                }

                return made;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Failure<bool>(ex.Message, ex);
            }
        });
        _main.BusyText = null;
        Problem = saved.IsSuccess ? null : saved.Error;
        _main.Status = saved.IsSuccess ? $"Saved {dialog.FileName}" : "The movie wasn't saved.";
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveFrameAsync(double seconds)
    {
        VideoEntry entry = Selected!;
        var dialog = new SaveFileDialog { Title = "Save this frame", FileName = entry.Title.Replace(' ', '_') + ".png", Filter = "PNG images (*.png)|*.png" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string ffmpeg = _main.FfmpegPath!;
        GameVideo video = entry.Video;
        Result<bool> saved = await Task.Run(() => GameVideos.SaveFrame(video, seconds, video.Width, dialog.FileName, ffmpeg));
        Problem = saved.IsSuccess ? null : saved.Error;
        _main.Status = saved.IsSuccess ? $"Saved {dialog.FileName}" : "The frame wasn't saved.";
    }
}
