using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Tdb;

public enum TdbFieldKind
{
    String = 0,
    Binary = 1,
    SignedInt = 2,
    UnsignedInt = 3,
    Float = 4,
    VarChar1 = 13,
    VarChar2 = 14,
    Unknown = -1,
}

/// <summary>One 16-byte field descriptor: kind, bit offset within the record, canonical 4-char id, bit width.</summary>
public sealed class TdbField
{
    internal TdbField(int index, uint kindCode, uint bitOffset, string id, uint bitWidth)
    {
        Index = index;
        KindCode = kindCode;
        Kind = kindCode is 0 or 1 or 2 or 3 or 4 or 13 or 14 ? (TdbFieldKind)kindCode : TdbFieldKind.Unknown;
        BitOffset = (int)bitOffset;
        Id = id;
        BitWidth = (int)bitWidth;
    }

    public int Index { get; }

    public uint KindCode { get; }

    public TdbFieldKind Kind { get; }

    public int BitOffset { get; }

    /// <summary>Canonical id (the byte-reversed on-disk id) -- the same spelling Heck's editor and the Studio defs use.</summary>
    public string Id { get; }

    public int BitWidth { get; }

    public bool IsInlineText => Kind == TdbFieldKind.String;

    public bool IsNumeric => Kind is TdbFieldKind.UnsignedInt or TdbFieldKind.SignedInt;
}

/// <summary>
/// One table of a TDB file. Field reads/writes operate directly on the owning file's working
/// buffer, so an edit is a few changed bits -- nothing else in the file is rebuilt or moved.
/// </summary>
public sealed class TdbTable
{
    private readonly TdbFile _file;
    private readonly Dictionary<string, TdbField> _fieldsById = new(StringComparer.Ordinal);

    internal TdbTable(TdbFile file, string id, int headerOffset)
    {
        _file = file;
        Id = id;
        HeaderOffset = headerOffset;
    }

    public string Id { get; }

    internal int HeaderOffset { get; }

    public uint AllocationType { get; internal set; }

    public int RecordBytes { get; internal set; }

    public int MaxRecords { get; internal set; }

    public int RowCount { get; internal set; }

    internal int FieldStart { get; set; }

    internal int DataStart { get; set; }

    public IReadOnlyList<TdbField> Fields { get; internal set; } = Array.Empty<TdbField>();

    internal void IndexFields()
    {
        _fieldsById.Clear();
        foreach (TdbField field in Fields)
        {
            _fieldsById.TryAdd(field.Id, field);
        }
    }

    public TdbField? FindField(string id) => _fieldsById.GetValueOrDefault(id);

    private int RowOffset(int row)
    {
        if ((uint)row >= (uint)RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        return DataStart + (row * RecordBytes);
    }

    /// <summary>Raw (unsigned) bits of a numeric field. Sign interpretation belongs to the meta layer.</summary>
    public ulong GetRaw(int row, TdbField field) =>
        TdbBits.Read(_file.Buffer.AsSpan(RowOffset(row), RecordBytes), field.BitOffset, field.BitWidth);

    /// <summary>Writes raw bits; false (and no change) if the value doesn't fit the field's bit width or the field isn't numeric.</summary>
    public bool TrySetRaw(int row, TdbField field, ulong value, out string? error)
    {
        error = null;
        if (!field.IsNumeric || field.BitWidth is < 1 or > 64)
        {
            error = $"Field '{field.Id}' is not a numeric field.";
            return false;
        }

        if (field.BitWidth < 64 && value >> field.BitWidth != 0)
        {
            error = $"Value {value} doesn't fit in {field.BitWidth} bit(s).";
            return false;
        }

        TdbBits.Write(_file.Buffer.AsSpan(RowOffset(row), RecordBytes), field.BitOffset, field.BitWidth, value);
        return true;
    }

    /// <summary>Spare rows the file already has room for (max_records - current_records). Growing past this would mean moving later tables, which this writer never does.</summary>
    public int SpareRows => MaxRecords - RowCount;

    /// <summary>
    /// Inserts an empty (all-zero) record at <paramref name="position"/>, shifting later rows down by
    /// one, using the table's own pre-allocated spare rows (the game pads free rows with 0xDB). Only
    /// valid for fixed-record tables (allocation type 2/6/10/14) whose record block already has room;
    /// updates current_records in the table header.
    /// </summary>
    public bool TryInsertRow(int position, out string? error)
    {
        error = null;
        if (AllocationType is not (2 or 6 or 10 or 14))
        {
            error = $"Table '{Id}' has allocation type {AllocationType}; only fixed-record tables can take new rows.";
            return false;
        }

        if (RowCount >= MaxRecords)
        {
            error = $"Table '{Id}' is full ({MaxRecords} rows) and can't grow without moving other tables.";
            return false;
        }

        if (position < 0 || position > RowCount)
        {
            error = "Insert position is out of range.";
            return false;
        }

        byte[] buf = _file.Buffer;
        int start = DataStart + (position * RecordBytes);
        int end = DataStart + (RowCount * RecordBytes);
        Buffer.BlockCopy(buf, start, buf, start + RecordBytes, end - start);
        Array.Clear(buf, start, RecordBytes);

        RowCount++;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(HeaderOffset + 22), (ushort)RowCount);
        return true;
    }

    public byte[] GetRecordBytes(int row) => _file.Buffer.AsSpan(RowOffset(row), RecordBytes).ToArray();

    public void SetRecordBytes(int row, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordBytes)
        {
            throw new ArgumentException("Record size mismatch.", nameof(bytes));
        }

        bytes.CopyTo(_file.Buffer.AsSpan(RowOffset(row), RecordBytes));
    }

    public string GetText(int row, TdbField field)
    {
        if (!field.IsInlineText || field.BitOffset % 8 != 0 || field.BitWidth % 8 != 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> bytes = _file.Buffer.AsSpan(RowOffset(row) + (field.BitOffset / 8), field.BitWidth / 8);
        int len = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(len < 0 ? bytes : bytes[..len]);
    }

    /// <summary>Writes an inline (fixed-size) string, NUL-padded; false (no change) if it doesn't fit.</summary>
    public bool TrySetText(int row, TdbField field, string text, out string? error)
    {
        error = null;
        if (!field.IsInlineText || field.BitOffset % 8 != 0 || field.BitWidth % 8 != 0)
        {
            error = $"Field '{field.Id}' isn't an inline text field.";
            return false;
        }

        byte[] encoded = Encoding.UTF8.GetBytes(text);
        int capacity = field.BitWidth / 8;
        if (encoded.Length > capacity)
        {
            error = $"Text is {encoded.Length} byte(s) but the field holds {capacity}.";
            return false;
        }

        Span<byte> target = _file.Buffer.AsSpan(RowOffset(row) + (field.BitOffset / 8), capacity);
        target.Clear();
        encoded.CopyTo(target);
        return true;
    }
}

/// <summary>
/// Reader/writer for EA's "TDB v6" database container (the .db files NHL Legacy / NHL 14 /
/// NHL 15 use on Xbox 360 and PS3). Layout, bit packing and checksum rules follow the NHL
/// Modding Studio's verified spec (docs/formats/tdb-v6-format-spec.md) and are re-checked here
/// against the real game files: a no-op re-save must reproduce the input byte-for-byte (see
/// TdbFileRealFileTests).
///
/// Scope is deliberately narrow: big-endian files only, and edits only change bits inside
/// existing fixed-width records. Variable-length string pools (allocation types 34/66) are
/// read through untouched -- rebuilding the Huffman pool is separate, riskier work.
/// </summary>
public sealed class TdbFile
{
    private const int HeaderSize = 0x18;
    private const int DirectoryEntrySize = 8;
    private const int TableHeaderSize = 0x28;
    private const int FieldDescriptorSize = 0x10;

    private readonly List<TdbTable> _tables = new();
    private int _directoryEnd;

    private TdbFile(byte[] buffer)
    {
        Buffer = buffer;
    }

    internal byte[] Buffer { get; }

    public IReadOnlyList<TdbTable> Tables => _tables;

    public TdbTable? FindTable(string id) => _tables.FirstOrDefault(t => t.Id == id);

    public static Result<TdbFile> Parse(byte[] data)
    {
        try
        {
            if (data.Length < HeaderSize + 4 || data[0] != (byte)'D' || data[1] != (byte)'B')
            {
                return Result.Failure<TdbFile>("Not a TDB database (missing 'DB' magic).");
            }

            if (data[4] != 1)
            {
                return Result.Failure<TdbFile>("Only big-endian (Xbox 360 / PS3 / Legacy) TDB files are supported; this file is little-endian.");
            }

            uint dbSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            if (dbSize != data.Length)
            {
                return Result.Failure<TdbFile>($"Header says the database is {dbSize} byte(s) but the file is {data.Length}.");
            }

            var file = new TdbFile((byte[])data.Clone());
            uint tableCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            if (tableCount > 4096 || HeaderSize + (tableCount * DirectoryEntrySize) > data.Length)
            {
                return Result.Failure<TdbFile>($"Implausible table count {tableCount}.");
            }

            int directoryEnd = HeaderSize + (int)(tableCount * DirectoryEntrySize);
            file._directoryEnd = directoryEnd;

            for (int i = 0; i < tableCount; i++)
            {
                int entry = HeaderSize + (i * DirectoryEntrySize);
                string id = ReadId(data, entry);
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 4));
                long headerOffset = directoryEnd + (long)offset;
                if (headerOffset + TableHeaderSize > data.Length - 4)
                {
                    return Result.Failure<TdbFile>($"Table '{id}' header lies outside the file.");
                }

                var table = new TdbTable(file, id, (int)headerOffset);
                int hp = (int)headerOffset;
                table.AllocationType = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(hp + 4));
                table.RecordBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(hp + 8));
                table.MaxRecords = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hp + 20));
                table.RowCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hp + 22));
                int fieldCount = data[hp + 28];
                table.FieldStart = hp + TableHeaderSize;
                table.DataStart = table.FieldStart + (fieldCount * FieldDescriptorSize);

                long recordsEnd = table.DataStart + ((long)table.RecordBytes * table.RowCount);
                if (recordsEnd > data.Length - 4)
                {
                    return Result.Failure<TdbFile>($"Table '{id}' records run past the end of the file.");
                }

                var fields = new List<TdbField>(fieldCount);
                for (int f = 0; f < fieldCount; f++)
                {
                    int fp = table.FieldStart + (f * FieldDescriptorSize);
                    uint kind = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(fp));
                    uint bitOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(fp + 4));
                    string fieldId = ReadId(data, fp + 8);
                    uint bitWidth = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(fp + 12));
                    fields.Add(new TdbField(f, kind, bitOffset, fieldId, bitWidth));
                }

                table.Fields = fields;
                table.IndexFields();
                file._tables.Add(table);
            }

            return Result.Success(file);
        }
        catch (Exception ex)
        {
            return Result.Failure<TdbFile>($"Failed to parse TDB file: {ex.Message}", ex);
        }
    }

    /// <summary>Canonical id = on-disk bytes reversed (big-endian files).</summary>
    private static string ReadId(byte[] data, int offset) =>
        new(new[] { (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset] });

    /// <summary>The working buffer with every checksum recomputed -- what should be written to disk.</summary>
    public byte[] ToBytes()
    {
        var copy = (byte[])Buffer.Clone();
        ApplyChecksums(copy);
        return copy;
    }

    /// <summary>Checksum slots whose stored value doesn't match what the current bytes imply (empty = file is consistent).</summary>
    public IReadOnlyList<string> VerifyChecksums()
    {
        var expected = (byte[])Buffer.Clone();
        ApplyChecksums(expected);

        var mismatches = new List<string>();

        void Check(string label, int offset)
        {
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(Buffer.AsSpan(offset));
            uint want = BinaryPrimitives.ReadUInt32BigEndian(expected.AsSpan(offset));
            if (stored != want)
            {
                mismatches.Add($"{label} @0x{offset:X}: stored {stored:X8}, expected {want:X8}");
            }
        }

        Check("file header", 0x14);
        foreach (TdbTable table in _tables)
        {
            Check($"table {table.Id} prior-body", table.HeaderOffset);
            Check($"table {table.Id} header", table.HeaderOffset + 0x24);
        }

        Check("end of file", Buffer.Length - 4);
        return mismatches;
    }

    private void ApplyChecksums(byte[] buf)
    {
        List<TdbTable> ordered = _tables.OrderBy(t => t.HeaderOffset).ToList();

        uint prior = TdbCrc.Compute(buf.AsSpan(HeaderSize, _directoryEnd - HeaderSize));
        for (int i = 0; i < ordered.Count; i++)
        {
            TdbTable table = ordered[i];
            int hp = table.HeaderOffset;

            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(hp), prior);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(hp + 0x24), TdbCrc.Compute(buf.AsSpan(hp + 4, 32)));

            int bodyStart = hp + TableHeaderSize;
            int bodyEnd = BodyEnd(table, i + 1 < ordered.Count ? ordered[i + 1].HeaderOffset : buf.Length - 4);
            prior = TdbCrc.Compute(buf.AsSpan(bodyStart, bodyEnd - bodyStart));
        }

        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(buf.Length - 4), prior);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x14), TdbCrc.Compute(buf.AsSpan(0, 0x14)));
    }

    /// <summary>
    /// A table's checksummed body runs to the start of the next table (the file's EOF CRC for the last one) --
    /// the rule that matches every table of the game database AND of roster saves. (The spec's per-allocation-type
    /// "start + record bytes x max records" only agrees with this where tables are packed; roster saves have
    /// tables with slack after their record area.)
    /// </summary>
    private static int BodyEnd(TdbTable table, int nextBoundary) => nextBoundary;
}
