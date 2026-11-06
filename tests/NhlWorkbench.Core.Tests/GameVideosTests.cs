using NhlWorkbench.Core.Tests.TestData;
using NhlWorkbench.Core.Videos;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class GameVideosTests
{
    private static readonly string Install = Path.Combine(SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native");
    private static readonly string Ffmpeg = Path.Combine(Install, "ffmpeg", "ffmpeg.exe");

    [Fact]
    public void TheInstallsMoviesAreFoundWithTheirFacts()
    {
        if (!Directory.Exists(Path.Combine(Install, "data", "movies")))
        {
            return;
        }

        IReadOnlyList<GameVideo> videos = GameVideos.Find(Install);
        Assert.Equal(50, videos.Count);
        Assert.All(videos, v =>
        {
            Assert.True(v.Width > 0 && v.Height > 0, v.Name);
            Assert.True(v.Seconds > 0, v.Name);
            Assert.NotEmpty(v.Archive);
        });
        GameVideo first = videos.First(v => v.Name == "cache 11C82C20");
        Assert.Equal(1280, first.Width);
        Assert.Equal(720, first.Height);
        Assert.Equal(7.5, first.Seconds, 1);
    }

    [Fact]
    public void AFrameAndAnMp4CanBeMade()
    {
        if (!File.Exists(Ffmpeg))
        {
            return;
        }

        GameVideo video = GameVideos.Find(Install).OrderBy(v => v.Size).First();
        string folder = Directory.CreateTempSubdirectory("video-test").FullName;
        try
        {
            string png = Path.Combine(folder, "frame.png");
            var frame = GameVideos.SaveFrame(video, 0.5, 200, png, Ffmpeg);
            Assert.True(frame.IsSuccess, frame.Error);
            Assert.True(new FileInfo(png).Length > 100);

            string mp4 = Path.Combine(folder, "movie.mp4");
            var made = GameVideos.SaveMp4(video, mp4, Ffmpeg);
            Assert.True(made.IsSuccess, made.Error);
            Assert.True(new FileInfo(mp4).Length > 1000);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void WhatIsntAMovieIsLeftOut()
    {
        string folder = Directory.CreateTempSubdirectory("video-test").FullName;
        try
        {
            string movies = Path.Combine(folder, "data", "movies");
            Directory.CreateDirectory(movies);
            File.WriteAllBytes(Path.Combine(movies, "junk_0.vp6"), new byte[64]);
            File.WriteAllBytes(Path.Combine(movies, "short_0.vp6"), [1, 2]);
            Assert.Empty(GameVideos.Find(folder));
            Assert.Empty(GameVideos.Find(Path.Combine(folder, "nothing")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
