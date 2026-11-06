using System.Reflection;
using System.Xml.Linq;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Tdb;

public sealed class TdbFieldMeta
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required bool IsText { get; init; }

    public required int Depth { get; init; }

    public required long RangeLow { get; init; }

    public required long RangeHigh { get; init; }

    /// <summary>
    /// The descriptor carries no sign, so the schema range decides how raw bits map to a value:
    /// <list type="bullet">
    /// <item>range fits two's complement at this width (e.g. -50..50 in 8 bits) -> two's complement;</item>
    /// <item>range starts at -1 but is too big for that (e.g. brand -1..254 in 8 bits, equipment -1..14 in 4) -> raw 0..max are
    /// the value itself and the all-ones pattern means -1 ("none"). Treating these as two's complement would turn
    /// helmet type 8 into -8, which a real database showed immediately;</item>
    /// <item>otherwise unsigned.</item>
    /// </list>
    /// </summary>
    public bool IsTwosComplement =>
        RangeLow < 0 && Depth is >= 1 and < 64 && RangeLow >= -(1L << (Depth - 1)) && RangeHigh <= (1L << (Depth - 1)) - 1;

    public bool IsAllOnesNull => RangeLow == -1 && !IsTwosComplement && Depth is >= 1 and < 64;

    public bool IsSigned => RangeLow < 0;

    public long Decode(ulong raw)
    {
        if (IsTwosComplement && (raw & (1UL << (Depth - 1))) != 0)
        {
            return (long)raw - (1L << Depth);
        }

        if (IsAllOnesNull && raw == (1UL << Depth) - 1)
        {
            return -1;
        }

        return (long)raw;
    }

    public bool TryEncode(long value, out ulong raw)
    {
        raw = 0;
        if (value < RangeLow || value > RangeHigh)
        {
            return false;
        }

        if (value >= 0)
        {
            raw = (ulong)value;
        }
        else if (IsTwosComplement)
        {
            raw = (ulong)(value + (1L << Depth));
        }
        else if (IsAllOnesNull && value == -1)
        {
            raw = (1UL << Depth) - 1;
        }
        else
        {
            return false;
        }

        return true;
    }
}

public sealed class TdbTableMeta
{
    private readonly Dictionary<string, TdbFieldMeta> _byName;
    private readonly Dictionary<string, TdbFieldMeta> _byId;

    internal TdbTableMeta(string id, string name, IReadOnlyList<TdbFieldMeta> fields)
    {
        Id = id;
        Name = name;
        Fields = fields;
        _byName = fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byId = fields.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyList<TdbFieldMeta> Fields { get; }

    public TdbFieldMeta? FindByName(string name) => _byName.GetValueOrDefault(name);

    public TdbFieldMeta? FindById(string id) => _byId.GetValueOrDefault(id);
}

/// <summary>
/// The database's own schema (nhlng-meta.xml): real table/field names, bit depth and value range
/// per field. Its shortnames are the byte-reverse of the ids stored in big-endian files, so they
/// are flipped on load to the same canonical spelling <see cref="TdbFile"/> uses.
/// </summary>
public sealed class TdbMeta
{
    private const string EmbeddedName = "NhlWorkbench.Core.Tdb.nhlng-meta.xml";
    private static readonly Lazy<TdbMeta> DefaultMeta = new(() => LoadEmbedded());

    private readonly Dictionary<string, TdbTableMeta> _tables;

    private TdbMeta(Dictionary<string, TdbTableMeta> tables)
    {
        _tables = tables;
    }

    /// <summary>Schema bundled with the app (NHL Legacy's nhlng database).</summary>
    public static TdbMeta Default => DefaultMeta.Value;

    public IReadOnlyCollection<TdbTableMeta> Tables => _tables.Values;

    public TdbTableMeta? FindTable(string id) => _tables.GetValueOrDefault(id);

    public static TdbMeta Load(Stream stream)
    {
        XDocument doc = XDocument.Load(stream);
        var tables = new Dictionary<string, TdbTableMeta>(StringComparer.Ordinal);

        foreach (XElement table in doc.Descendants("table"))
        {
            string shortName = (string?)table.Attribute("shortname") ?? string.Empty;
            if (shortName.Length != 4)
            {
                continue;
            }

            var fields = new List<TdbFieldMeta>();
            foreach (XElement field in table.Descendants("field"))
            {
                string fieldShort = (string?)field.Attribute("shortname") ?? string.Empty;
                if (fieldShort.Length != 4)
                {
                    continue;
                }

                fields.Add(new TdbFieldMeta
                {
                    Id = Reverse(fieldShort),
                    Name = (string?)field.Attribute("name") ?? fieldShort,
                    IsText = ((string?)field.Attribute("type"))?.Contains("STRING", StringComparison.OrdinalIgnoreCase) == true,
                    Depth = (int?)field.Attribute("depth") ?? 0,
                    RangeLow = (long?)field.Attribute("rangelow") ?? 0,
                    RangeHigh = (long?)field.Attribute("rangehigh") ?? 0,
                });
            }

            string id = Reverse(shortName);
            tables[id] = new TdbTableMeta(id, (string?)table.Attribute("name") ?? shortName, fields);
        }

        return new TdbMeta(tables);
    }

    private static TdbMeta LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedName)
            ?? throw new InvalidOperationException($"Embedded schema '{EmbeddedName}' is missing from NhlWorkbench.Core.");
        return Load(stream);
    }

    private static string Reverse(string value)
    {
        char[] chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>
    /// Compares a file's real field descriptors with this schema. A mismatch (different bit width
    /// or offset layout for a field we edit) means the file isn't the layout this schema describes,
    /// so edits to that table must not be trusted. Empty = every shared field agrees.
    /// </summary>
    public IReadOnlyList<string> Validate(TdbFile file)
    {
        var problems = new List<string>();
        foreach (TdbTable table in file.Tables)
        {
            TdbTableMeta? meta = FindTable(table.Id);
            if (meta is null)
            {
                problems.Add($"Table '{table.Id}' isn't in the bundled schema.");
                continue;
            }

            foreach (TdbField field in table.Fields)
            {
                TdbFieldMeta? fieldMeta = meta.FindById(field.Id);
                if (fieldMeta is null)
                {
                    problems.Add($"{meta.Name}: field '{field.Id}' isn't in the bundled schema.");
                }
                else if (fieldMeta.Depth != field.BitWidth)
                {
                    problems.Add($"{meta.Name}.{fieldMeta.Name}: file says {field.BitWidth} bit(s), schema says {fieldMeta.Depth}.");
                }
            }
        }

        return problems;
    }
}

/// <summary>A table plus its schema: read/write fields by their real names with range checking and sign handling.</summary>
public sealed class TdbTableView
{
    internal TdbTableView(TdbTable table, TdbTableMeta meta)
    {
        Table = table;
        Meta = meta;
    }

    public TdbTable Table { get; }

    public TdbTableMeta Meta { get; }

    public int RowCount => Table.RowCount;

    public int SpareRows => Table.SpareRows;

    public bool TryInsertRow(int position, out string? error) => Table.TryInsertRow(position, out error);

    /// <summary>Swaps two rows' bytes, then restores the named field's values to their original positions -- i.e. exchanges
    /// everything about two records except their position-bound sort key (e.g. jerseyindex).</summary>
    public void SwapRowsKeepingField(int a, int b, string keepFieldName)
    {
        long keepA = GetInt(a, keepFieldName);
        long keepB = GetInt(b, keepFieldName);
        byte[] ra = Table.GetRecordBytes(a);
        byte[] rb = Table.GetRecordBytes(b);
        Table.SetRecordBytes(a, rb);
        Table.SetRecordBytes(b, ra);
        TrySetInt(a, keepFieldName, keepA, out _);
        TrySetInt(b, keepFieldName, keepB, out _);
    }

    /// <summary>Copies every field of <paramref name="source"/> into <paramref name="target"/> (same table).</summary>
    public void CopyRow(int source, int target) => Table.SetRecordBytes(target, Table.GetRecordBytes(source));

    public bool HasField(string name) => Resolve(name, out _, out _);

    private bool Resolve(string name, out TdbField field, out TdbFieldMeta meta)
    {
        TdbFieldMeta? m = Meta.FindByName(name);
        TdbField? f = m is null ? null : Table.FindField(m.Id);
        if (m is null || f is null)
        {
            field = null!;
            meta = null!;
            return false;
        }

        field = f;
        meta = m;
        return true;
    }

    public long GetInt(int row, string name)
    {
        if (!Resolve(name, out TdbField field, out TdbFieldMeta meta))
        {
            throw new KeyNotFoundException($"Field '{name}' not found in table '{Meta.Name}'.");
        }

        return meta.Decode(Table.GetRaw(row, field));
    }

    public bool TrySetInt(int row, string name, long value, out string? error)
    {
        if (!Resolve(name, out TdbField field, out TdbFieldMeta meta))
        {
            error = $"Field '{name}' not found in table '{Meta.Name}'.";
            return false;
        }

        if (!meta.TryEncode(value, out ulong raw))
        {
            error = $"{meta.Name} must be between {meta.RangeLow} and {meta.RangeHigh}.";
            return false;
        }

        return Table.TrySetRaw(row, field, raw, out error);
    }

    public string GetText(int row, string name) =>
        Resolve(name, out TdbField field, out _) ? Table.GetText(row, field) : string.Empty;

    public bool TrySetText(int row, string name, string text, out string? error)
    {
        if (!Resolve(name, out TdbField field, out _))
        {
            error = $"Field '{name}' not found in table '{Meta.Name}'.";
            return false;
        }

        return Table.TrySetText(row, field, text, out error);
    }
}

/// <summary>
/// A loaded NHL Legacy-style game database plus its schema. Opening it checks the file's real
/// layout against the schema and its stored checksums, so the editor can refuse (or warn) before
/// touching a file that isn't what we think it is.
/// </summary>
public sealed class TdbDatabase
{
    public const string TeamsTableId = "kOtt";
    public const string EquipmentTableId = "hHbv";
    public const string JerseysTableId = "xjgw";

    private TdbDatabase(TdbFile file, TdbMeta meta, IReadOnlyList<string> schemaProblems, IReadOnlyList<string> checksumProblems)
    {
        File = file;
        Meta = meta;
        SchemaProblems = schemaProblems;
        ChecksumProblems = checksumProblems;
    }

    public TdbFile File { get; }

    public TdbMeta Meta { get; }

    /// <summary>Differences between this file's layout and the bundled schema (empty = matches).</summary>
    public IReadOnlyList<string> SchemaProblems { get; }

    /// <summary>Stored checksums that didn't match on load (empty = file was internally consistent).</summary>
    public IReadOnlyList<string> ChecksumProblems { get; }

    public static Result<TdbDatabase> Open(byte[] data, TdbMeta? meta = null)
    {
        Result<TdbFile> parsed = TdbFile.Parse(data);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<TdbDatabase>(parsed.Error!);
        }

        meta ??= TdbMeta.Default;
        TdbFile file = parsed.Value!;
        return Result.Success(new TdbDatabase(file, meta, meta.Validate(file), file.VerifyChecksums()));
    }

    public TdbTableView? GetTable(string tableId)
    {
        TdbTable? table = File.FindTable(tableId);
        TdbTableMeta? tableMeta = Meta.FindTable(tableId);
        return table is null || tableMeta is null ? null : new TdbTableView(table, tableMeta);
    }

    public byte[] ToBytes() => File.ToBytes();
}
