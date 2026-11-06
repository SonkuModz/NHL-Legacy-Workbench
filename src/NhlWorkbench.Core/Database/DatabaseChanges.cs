namespace NhlWorkbench.Core.Database;

/// <summary>One cell that holds something different from the copy it was compared with.</summary>
public sealed record DatabaseCellChange(
    string TableId,
    string TableName,
    int Row,
    string RowLabel,
    string ColumnId,
    string ColumnName,
    int ColumnIndex,
    string Now,
    string Original);

/// <summary>What differs between a database and the copy it was compared with.</summary>
/// <param name="Cells">The changed cells, in table order.</param>
/// <param name="IsCut">True when there were more changes than the limit, so the list stops early.</param>
/// <param name="Notes">Differences that aren't cells, such as a table with more rows.</param>
public sealed record DatabaseChanges(IReadOnlyList<DatabaseCellChange> Cells, bool IsCut, IReadOnlyList<string> Notes)
{
    public static readonly DatabaseChanges None = new([], false, []);
}

/// <summary>Compares a database with another copy of it, and puts single cells back.</summary>
public static class DatabaseComparer
{
    public const int MostCells = 50_000;

    /// <summary>Only cells that can be edited count, since those are the only ones that can be shown or put back.</summary>
    public static DatabaseChanges Compare(GameDatabase now, GameDatabase original, int mostCells = MostCells)
    {
        if (now.DatabaseBytes().AsSpan().SequenceEqual(original.DatabaseBytes()))
        {
            return DatabaseChanges.None;
        }

        var cells = new List<DatabaseCellChange>();
        var notes = new List<string>();
        Dictionary<string, DatabaseTable> others = original.Tables.ToDictionary(t => t.Id);
        foreach (DatabaseTable table in now.Tables)
        {
            if (!others.TryGetValue(table.Id, out DatabaseTable? other))
            {
                notes.Add($"{table.Name} isn't in the other copy.");
                continue;
            }

            if (table.RowCount != other.RowCount)
            {
                notes.Add($"{table.Name} has {table.RowCount:N0} rows here and {other.RowCount:N0} in the other copy. Only the rows in both are compared.");
            }

            if (!CompareTable(table, other, cells, mostCells))
            {
                return new DatabaseChanges(cells, true, notes);
            }
        }

        return new DatabaseChanges(cells, false, notes);
    }

    /// <summary>Puts the original's value back in one cell. Null when it went in, otherwise why it can't.</summary>
    public static string? PutBack(DatabaseTable now, DatabaseTable original, int row, DatabaseColumn column)
    {
        if (original.FindColumn(column.Id) is not { } other || !column.IsEditable || row < 0 || row >= now.RowCount || row >= original.RowCount)
        {
            return $"The other copy has no {column.Name} on row {row}.";
        }

        // By the stored bits, so a value outside the range the page allows typing in can still go back.
        if (column.IsText)
        {
            return now.Table.TrySetText(row, column.Field, original.Table.GetText(row, other.Field), out string? textError) ? null : textError;
        }

        return now.Table.TrySetRaw(row, column.Field, original.Table.GetRaw(row, other.Field), out string? error) ? null : error;
    }

    private static bool CompareTable(DatabaseTable table, DatabaseTable other, List<DatabaseCellChange> cells, int mostCells)
    {
        int rows = Math.Min(table.RowCount, other.RowCount);
        for (int c = 0; c < table.Columns.Count; c++)
        {
            DatabaseColumn column = table.Columns[c];
            if (!column.IsEditable || other.FindColumn(column.Id) is not { IsEditable: true } theirs || theirs.IsText != column.IsText)
            {
                continue;
            }

            for (int row = 0; row < rows; row++)
            {
                bool same = column.IsText
                    ? table.Table.GetText(row, column.Field) == other.Table.GetText(row, theirs.Field)
                    : table.Table.GetRaw(row, column.Field) == other.Table.GetRaw(row, theirs.Field);
                if (same)
                {
                    continue;
                }

                if (cells.Count >= mostCells)
                {
                    return false;
                }

                cells.Add(new DatabaseCellChange(table.Id, table.Name, row, RowLabel(table, row), column.Id, column.Name, c, table.GetCell(row, column), other.GetCell(row, theirs)));
            }
        }

        return true;
    }

    /// <summary>The row's first text, so a change can say what it's on, not only a number.</summary>
    private static string RowLabel(DatabaseTable table, int row)
    {
        foreach (DatabaseColumn column in table.Columns)
        {
            if (column.IsText && table.GetCell(row, column).Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
