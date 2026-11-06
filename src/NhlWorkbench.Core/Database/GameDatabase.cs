using System.Globalization;
using System.Text;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Database;

/// <summary>A column of a table, named from the game's own schema where it has one.</summary>
public sealed class DatabaseColumn
{
    internal DatabaseColumn(TdbField field, TdbFieldMeta? meta)
    {
        Field = field;
        Meta = meta;
    }

    internal TdbField Field { get; }

    internal TdbFieldMeta? Meta { get; }

    /// <summary>The field's four-letter id, which is unique within a table.</summary>
    public string Id => Field.Id;

    public string Name => Meta?.Name ?? Field.Id;

    public bool IsText => Field.IsInlineText;

    public bool IsEditable => Field.IsInlineText || Field.IsNumeric;

    /// <summary>What can go in this column, for a tooltip.</summary>
    public string Hint
    {
        get
        {
            if (IsText)
            {
                return $"Text, up to {Field.BitWidth / 8} bytes";
            }

            if (!Field.IsNumeric)
            {
                return "Can't be edited here";
            }

            (long low, long high) = Range;
            return $"Number, {low} to {high}";
        }
    }

    /// <summary>The values allowed: the schema's range limited by what the field's bits can hold.</summary>
    public (long Low, long High) Range
    {
        get
        {
            long bitsHigh = Field.BitWidth >= 63 ? long.MaxValue : (1L << Field.BitWidth) - 1;
            if (Meta is null)
            {
                return (0, bitsHigh);
            }

            // Some schema ranges are placeholders wider than the bits. Encoding refuses those, so they aren't offered.
            long high = Math.Min(Meta.RangeHigh, Meta.IsSigned && Meta.IsTwosComplement ? bitsHigh >> 1 : bitsHigh);
            return (Meta.RangeLow, high);
        }
    }
}

/// <summary>One table of a database.</summary>
public sealed class DatabaseTable
{
    internal DatabaseTable(TdbTable table, string name, IReadOnlyList<DatabaseColumn> columns)
    {
        Table = table;
        Name = name;
        Columns = columns;
    }

    internal TdbTable Table { get; }

    public string Id => Table.Id;

    public string Name { get; }

    public int RowCount => Table.RowCount;

    public int MaxRows => Table.MaxRecords;

    public IReadOnlyList<DatabaseColumn> Columns { get; }

    public DatabaseColumn? FindColumn(string idOrName) =>
        Columns.FirstOrDefault(c => c.Id == idOrName) ?? Columns.FirstOrDefault(c => c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public string GetCell(int row, DatabaseColumn column)
    {
        if (column.IsText)
        {
            return Table.GetText(row, column.Field);
        }

        if (!column.Field.IsNumeric || column.Field.BitWidth > 64)
        {
            return string.Empty;
        }

        ulong raw = Table.GetRaw(row, column.Field);
        return column.Meta is { } meta ? meta.Decode(raw).ToString(CultureInfo.InvariantCulture) : raw.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Null when the value went in, otherwise why it can't. Nothing changes on a refusal.</summary>
    public string? SetCell(int row, DatabaseColumn column, string text)
    {
        if (!column.IsEditable)
        {
            return $"{column.Name} can't be edited here.";
        }

        if (column.IsText)
        {
            return Table.TrySetText(row, column.Field, text, out string? textError) ? null : textError;
        }

        if (!long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            return $"{column.Name} has to be a whole number.";
        }

        (long low, long high) = column.Range;
        if (value < low || value > high)
        {
            return $"{column.Name} has to be from {low} to {high}.";
        }

        ulong raw;
        if (column.Meta is { } meta)
        {
            if (!meta.TryEncode(value, out raw))
            {
                return $"{column.Name} can't hold {value}.";
            }
        }
        else
        {
            raw = (ulong)value;
        }

        return Table.TrySetRaw(row, column.Field, raw, out string? error) ? null : error;
    }

    /// <summary>Rows where any cell contains the text, for searching a table.</summary>
    public bool RowContains(int row, string text)
    {
        foreach (DatabaseColumn column in Columns)
        {
            if (GetCell(row, column).Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The whole table as CSV, with the columns named on the first line and each row's number first.</summary>
    public string ToCsv()
    {
        var text = new StringBuilder();
        text.Append("row");
        foreach (DatabaseColumn column in Columns)
        {
            text.Append(',').Append(CsvText.Quote(column.Name));
        }

        text.Append("\r\n");
        for (int row = 0; row < RowCount; row++)
        {
            text.Append(row.ToString(CultureInfo.InvariantCulture));
            foreach (DatabaseColumn column in Columns)
            {
                text.Append(',').Append(CsvText.Quote(GetCell(row, column)));
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Applies a CSV made by <see cref="ToCsv"/>. Only changed cells are written. It checks every row first and applies
    /// nothing if any cell is wrong, so a bad file can't leave a table half-changed. Rows can't be added this way.
    /// </summary>
    public Result<int> ApplyCsv(string csv)
    {
        List<string[]> lines = CsvText.Parse(csv);
        if (lines.Count == 0 || lines[0].Length < 2 || !lines[0][0].Equals("row", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<int>("The first line has to name the columns, starting with \"row\".");
        }

        var columns = new DatabaseColumn?[lines[0].Length];
        for (int i = 1; i < columns.Length; i++)
        {
            columns[i] = FindColumn(lines[0][i].Trim());
            if (columns[i] is null)
            {
                return Result.Failure<int>($"This table has no column called \"{lines[0][i]}\".");
            }
        }

        var changes = new List<(int Row, DatabaseColumn Column, string Text, string Before)>();
        for (int line = 1; line < lines.Count; line++)
        {
            string[] cells = lines[line];
            if (cells.Length != columns.Length)
            {
                return Result.Failure<int>($"Line {line + 1} has {cells.Length} cells, not {columns.Length}.");
            }

            if (!int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row >= RowCount)
            {
                return Result.Failure<int>($"Line {line + 1} names row \"{cells[0]}\", which this table doesn't have.");
            }

            for (int i = 1; i < cells.Length; i++)
            {
                string before = GetCell(row, columns[i]!);
                if (cells[i] != before)
                {
                    changes.Add((row, columns[i]!, cells[i], before));
                }
            }
        }

        // Set each one to find any refusal, undoing them all if there is one.
        for (int i = 0; i < changes.Count; i++)
        {
            (int row, DatabaseColumn column, string value, _) = changes[i];
            if (SetCell(row, column, value) is { } error)
            {
                for (int undo = i - 1; undo >= 0; undo--)
                {
                    SetCell(changes[undo].Row, changes[undo].Column, changes[undo].Before);
                }

                return Result.Failure<int>($"Row {row}, {column.Name}: {error}");
            }
        }

        return Result.Success(changes.Count);
    }
}

/// <summary>
/// A game database opened for browsing and editing: <c>db/nhlng.db</c> and its siblings, or the database inside a roster
/// save. It works on any table, including ones the rest of the app knows nothing about.
/// </summary>
public sealed class GameDatabase
{
    private readonly TdbFile _file;
    private readonly TdbSaveContainer? _save;

    private GameDatabase(TdbFile file, TdbSaveContainer? save, IReadOnlyList<DatabaseTable> tables, IReadOnlyList<string> checksumProblems)
    {
        _file = file;
        _save = save;
        Tables = tables;
        ChecksumProblems = checksumProblems;
    }

    public IReadOnlyList<DatabaseTable> Tables { get; }

    public IReadOnlyList<string> ChecksumProblems { get; }

    public bool IsSave => _save is not null;

    public DatabaseTable? FindTable(string idOrName) =>
        Tables.FirstOrDefault(t => t.Id == idOrName) ?? Tables.FirstOrDefault(t => t.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a database, or a roster save if that's what the bytes are.</summary>
    public static Result<GameDatabase> Open(byte[] data)
    {
        try
        {
            TdbSaveContainer? save = null;
            byte[] database = data;
            if (TdbSaveContainer.LooksLikeContainer(data))
            {
                Result<TdbSaveContainer> parsed = TdbSaveContainer.Parse(data);
                if (!parsed.IsSuccess)
                {
                    return Result.Failure<GameDatabase>(parsed.Error!);
                }

                save = parsed.Value;
                database = save!.InnerDatabase;
            }

            Result<TdbFile> file = TdbFile.Parse(database);
            if (!file.IsSuccess)
            {
                return Result.Failure<GameDatabase>(file.Error!);
            }

            TdbMeta schema = TdbMeta.Default;
            var tables = new List<DatabaseTable>();
            foreach (TdbTable table in file.Value!.Tables)
            {
                TdbTableMeta? meta = schema.FindTable(table.Id);
                List<DatabaseColumn> columns = table.Fields.Select(f =>
                {
                    // A field whose width disagrees with the schema is shown under its id and read as plain bits.
                    TdbFieldMeta? fieldMeta = meta?.FindById(f.Id);
                    return new DatabaseColumn(f, fieldMeta is not null && fieldMeta.Depth == f.BitWidth ? fieldMeta : null);
                }).ToList();
                tables.Add(new DatabaseTable(table, meta?.Name ?? table.Id, columns));
            }

            tables.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return Result.Success(new GameDatabase(file.Value, save, tables, file.Value.VerifyChecksums()));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return Result.Failure<GameDatabase>($"Couldn't read this database: {ex.Message}", ex);
        }
    }

    /// <summary>The bytes to save: the database, or the rebuilt save around it.</summary>
    public Result<byte[]> Build() => Build(_file.ToBytes());

    /// <summary>The database's current bytes, copied so they can be packed away from the page.</summary>
    public byte[] DatabaseBytes() => _file.ToBytes();

    public Result<byte[]> Build(byte[] databaseBytes) =>
        _save is null ? Result.Success(databaseBytes) : _save.Build(databaseBytes);
}

/// <summary>The little bit of CSV the app reads and writes. Follows the usual quoting rules, so spreadsheets can open it.</summary>
public static class CsvText
{
    public static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (cell.Length > 0 && (cell[0] == ' ' || cell[^1] == ' '))
            ? "\"" + cell.Replace("\"", "\"\"") + "\""
            : cell;

    public static List<string[]> Parse(string csv)
    {
        var lines = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        int start = csv.StartsWith('﻿') ? 1 : 0;
        for (int i = start; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                cells.Add(cell.ToString());
                cell.Clear();
                if (cells.Count > 1 || cells[0].Length > 0)
                {
                    lines.Add(cells.ToArray());
                }

                cells.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            lines.Add(cells.ToArray());
        }

        return lines;
    }
}
