using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Mods;
using NhlWorkbench.Core.Tdb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class GameTextSheetTests
{
    private static (GameFileIndex Game, TdbLanguageFile File)? Open()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return null;
        }

        GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        return (game, TdbLanguageFile.Parse(game.ReadDecoded(game.Find("fe/loc/nhl_eng_us.db")!.Winner).Value!).Value!);
    }

    [Fact]
    public void TheDiscHasItsLanguagesWithEnglishFirst()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        using GameFileIndex game = opened.Game;
        IReadOnlyList<GameLanguage> languages = GameTextSheet.Languages(game);
        Assert.Equal(8, languages.Count);
        Assert.Equal("English", languages[0].Name);
        Assert.Contains(opened.File.Entries(), e => e.Key.StartsWith(GameTextSheet.TeamNamePrefix));
    }

    [Fact]
    public void ASheetComesBackWithNoChanges()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        using GameFileIndex game = opened.Game;
        string csv = GameTextSheet.ToCsv(opened.File);
        var applied = GameTextSheet.Apply(opened.File, csv);
        Assert.True(applied.IsSuccess, applied.Error);
        Assert.Equal(0, applied.Value!.Changed);
        Assert.Empty(applied.Value.Problems);
    }

    [Fact]
    public void ARenameSurvivesSavingAndBadLinesAreReported()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        using GameFileIndex game = opened.Game;
        TdbLanguageFile file = opened.File;
        string key = file.Entries().First(e => e.Key.StartsWith(GameTextSheet.TeamNamePrefix)).Key;
        string csv = $"Text,Key\r\nCafé,{key}\r\nNope,NoSuchKeyAtAll\r\nNew Name,{key}\r\n";

        var applied = GameTextSheet.Apply(file, csv);
        Assert.True(applied.IsSuccess, applied.Error);
        Assert.Equal(2, applied.Value!.Changed);
        Assert.Single(applied.Value.Problems);

        TdbLanguageFile again = TdbLanguageFile.Parse(file.ToBytes()).Value!;
        Assert.True(again.TryGetText(key, out string text));
        Assert.Equal("New Name", text);
        Assert.Equal(file.Count, again.Count);

        string only = GameTextSheet.ToCsv(again, new HashSet<string> { key });
        Assert.Equal(2, only.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void AFileWithoutTheColumnsIsRefused()
    {
        if (Open() is not { } opened)
        {
            return;
        }

        using GameFileIndex game = opened.Game;
        Assert.False(GameTextSheet.Apply(opened.File, "a,b\r\n1,2").IsSuccess);
        Assert.False(GameTextSheet.Apply(opened.File, string.Empty).IsSuccess);
    }
}
