using System.Diagnostics;
using System.Globalization;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Videos;

/// <summary>A movie the recomp's launcher pulled out of a game archive. These have picture only: their sound is elsewhere.</summary>
public sealed record GameVideo(string FilePath, string Archive, string Offset, long Size, int Width, int Height, int Frames, double FramesPerSecond)
{
    public string Name => $"{Archive} {Offset}";

    public double Seconds => FramesPerSecond > 0 ? Frames / FramesPerSecond : 0;
}

/// <summary>
/// Finds the game's movies, in <c>data/movies</c> of an install, and turns them into pictures and MP4s with the ffmpeg that
/// comes with the game. They can't be replaced: the launcher makes these files itself at install, and ffmpeg has no VP6
/// encoder to make new ones.
/// </summary>
public static class GameVideos
{
    public static IReadOnlyList<GameVideo> Find(string installRoot)
    {
        var videos = new List<GameVideo>();
        try
        {
            string folder = Path.Combine(installRoot, "data", "movies");
            if (!Directory.Exists(folder))
            {
                return videos;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.vp6"))
            {
                if (Read(path) is { } video)
                {
                    videos.Add(video);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was found so far is still worth showing.
        }

        return videos.OrderBy(v => v.Archive, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Offset, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads a movie's facts from its header. Null if it isn't one.</summary>
    public static GameVideo? Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[32];
            if (stream.Read(header) < 32 || header[0] != 'M' || header[1] != 'V' || header[2] != 'h' || header[3] != 'd')
            {
                return null;
            }

            int width = BitConverter.ToUInt16(header[12..]);
            int height = BitConverter.ToUInt16(header[14..]);
            int frames = BitConverter.ToInt32(header[16..]);
            uint rate = BitConverter.ToUInt32(header[24..]);
            uint scale = BitConverter.ToUInt32(header[28..]);
            string name = Path.GetFileNameWithoutExtension(path);
            int split = name.LastIndexOf('_');
            return new GameVideo(
                path,
                split > 0 ? name[..split] : name,
                split > 0 ? name[(split + 1)..] : string.Empty,
                stream.Length,
                width,
                height,
                frames,
                scale > 0 ? rate / (double)scale : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves one frame, at the given time, as a PNG of the given width.</summary>
    /// <remarks>
    /// The time goes after the input. These files have no seek table, so asking ffmpeg to jump ahead before opening one can
    /// finish cleanly with no picture at all. That's also why a missing file counts as a failure here.
    /// </remarks>
    public static Result<bool> SaveFrame(GameVideo video, double seconds, int width, string pngPath, string ffmpeg)
    {
        Result<bool> saved = Run(ffmpeg, "-i", video.FilePath, "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-frames:v", "1",
            "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-2", "-update", "1", pngPath);
        return saved.IsSuccess && !File.Exists(pngPath) ? Result.Failure<bool>("ffmpeg didn't find a picture at that time.") : saved;
    }

    /// <summary>
    /// Makes an MP4 that ordinary players can open: H.264 through Windows' own encoder, or plain MPEG-4 on a PC without one.
    /// </summary>
    public static Result<bool> SaveMp4(GameVideo video, string mp4Path, string ffmpeg)
    {
        Result<bool> h264 = Run(ffmpeg, "-i", video.FilePath, "-an", "-c:v", "h264_mf", "-b:v", "8M", "-pix_fmt", "yuv420p", mp4Path);
        return h264.IsSuccess ? h264 : Run(ffmpeg, "-i", video.FilePath, "-an", "-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p", mp4Path);
    }

    private static Result<bool> Run(string ffmpeg, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" }.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return Result.Failure<bool>("ffmpeg didn't start.");
            }

            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(errors, output);
            return process.ExitCode == 0 ? Result.Success(true) : Result.Failure<bool>(errors.Result.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<bool>(ex.Message, ex);
        }
    }
}
