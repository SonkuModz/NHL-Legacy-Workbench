using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class DatabaseChangesTests
{
    private static byte[]? MainDatabase()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return null;
        }

        using GameFileIndex game = GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value!;
        return game.ReadDecoded(game.Find("db/nhlng.db")!.Winner).Value;
    }

    [Fact]
    public void TwoCopiesOfTheSameFileHaveNoChanges()
    {
        if (MainDatabase() is not { } data)
        {
            return;
        }

        DatabaseChanges changes = DatabaseComparer.Compare(GameDatabase.Open(data).Value!, GameDatabase.Open(data).Value!);
        Assert.Empty(changes.Cells);
        Assert.False(changes.IsCut);
        Assert.Empty(changes.Notes);
    }

    [Fact]
    public void ChangedCellsAreFoundAndPutBack()
    {
        if (MainDatabase() is not { } data)
        {
            return;
        }

        GameDatabase now = GameDatabase.Open(data).Value!;
        GameDatabase original = GameDatabase.Open(data).Value!;
        DatabaseTable table = now.FindTable("exhibitionteams")!;
        DatabaseColumn text = table.Columns.First(c => c.IsText);
        DatabaseColumn number = table.Columns.First(c => c is { IsEditable: true, IsText: false } && c.Range.High > 1);
        string oldText = table.GetCell(2, text);
        string oldNumber = table.GetCell(3, number);
        string newNumber = oldNumber == "1" ? "0" : "1";
        Assert.Null(table.SetCell(2, text, "Abc"));
        Assert.Null(table.SetCell(3, number, newNumber));

        DatabaseChanges changes = DatabaseComparer.Compare(now, original);
        Assert.Equal(2, changes.Cells.Count);
        DatabaseCellChange textChange = changes.Cells.Single(c => c.Row == 2);
        Assert.Equal(("Abc", oldText, text.Id), (textChange.Now, textChange.Original, textChange.ColumnId));
        Assert.Same(text, table.Columns[textChange.ColumnIndex]);
        DatabaseCellChange numberChange = changes.Cells.Single(c => c.Row == 3);
        Assert.Equal((newNumber, oldNumber, table.Id), (numberChange.Now, numberChange.Original, numberChange.TableId));
        Assert.NotEmpty(numberChange.RowLabel);

        Assert.Null(DatabaseComparer.PutBack(table, original.FindTable(table.Id)!, 2, text));
        Assert.Equal(oldText, table.GetCell(2, text));
        Assert.Equal(3, DatabaseComparer.Compare(now, original).Cells.Single().Row);

        Assert.Null(DatabaseComparer.PutBack(table, original.FindTable(table.Id)!, 3, number));
        Assert.Empty(DatabaseComparer.Compare(now, original).Cells);
    }

    [Fact]
    public void TheListStopsAtTheLimitAndSaysSo()
    {
        if (MainDatabase() is not { } data)
        {
            return;
        }

        GameDatabase now = GameDatabase.Open(data).Value!;
        DatabaseTable table = now.FindTable("exhibitionteams")!;
        DatabaseColumn text = table.Columns.First(c => c.IsText);
        for (int row = 0; row < 3; row++)
        {
            Assert.Null(table.SetCell(row, text, "Abc" + row));
        }

        DatabaseChanges changes = DatabaseComparer.Compare(now, GameDatabase.Open(data).Value!, mostCells: 2);
        Assert.Equal(2, changes.Cells.Count);
        Assert.True(changes.IsCut);
    }

    [Fact]
    public void PuttingBackARowTheOtherCopyLacksIsRefused()
    {
        if (MainDatabase() is not { } data)
        {
            return;
        }

        GameDatabase now = GameDatabase.Open(data).Value!;
        DatabaseTable table = now.FindTable("exhibitionteams")!;
        Assert.NotNull(DatabaseComparer.PutBack(table, GameDatabase.Open(data).Value!.FindTable(table.Id)!, table.RowCount + 5, table.Columns.First(c => c.IsText)));
    }
}
