using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Tdb;

/// <summary>
/// Reader/writer for the game's language database (<c>nhl_eng_us.db</c> and its siblings): one
/// table of (stringid key, sourcetext) rows, sorted by a 32-bit hash of the key, whose strings live
/// in a Huffman-compressed pool after the records.
///
/// Facts below were reverse-engineered against the real files (not in any shipped doc) and are
/// each covered by TdbLanguageFileTests:
/// <list type="bullet">
/// <item>The row hash is CRC-32 (reflected, poly 0xEDB88320, init 0, no final XOR) over each key byte
/// ANDed with 0xDF -- 25,782 / 25,782 rows of the real English file match, and it makes keys
/// case-insensitive.</item>
/// <item>Rows are sorted by that hash as an UNSIGNED integer (even though the schema calls it signed).</item>
/// <item>Pool offsets are NOT in row order (the pool predates the sort), so existing strings are never
/// rewritten: edits and additions append new encoded strings and repoint/insert records. The old bytes
/// stay behind, unreferenced.</item>
/// <item>Strings are UTF-8 bytes; the tree has no escape symbol, so a character whose bytes aren't in the
/// file's tree can't be stored (reported, never silently dropped).</item>
/// </list>
/// Growing the file is safe here only because this is a single-table file -- nothing follows the pool
/// but the 4-byte end CRC.
/// </summary>
public sealed class TdbLanguageFile
{
    private const int RecordSize = 16;

    private sealed class Row
    {
        public required byte[] Raw { get; init; }

        public uint Hash => BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(8));

        public uint KeyOffset
        {
            get => BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(0));
            set => BinaryPrimitives.WriteUInt32BigEndian(Raw.AsSpan(0), value);
        }

        public uint TextOffset
        {
            get => BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(4));
            set => BinaryPrimitives.WriteUInt32BigEndian(Raw.AsSpan(4), value);
        }
    }

    private sealed class Node
    {
        public int Left = -1;
        public int Right = -1;
        public int Leaf = -1;
    }

    private readonly byte[] _prefix;
    private readonly List<Row> _rows;
    private readonly List<byte> _extra;
    private readonly List<Node> _nodes;
    private readonly Dictionary<byte, (uint Bits, int Length)> _codes = new();
    private readonly int _hashFieldRecordOffset = 8;
    private int _poolEnd;

    private TdbLanguageFile(byte[] prefix, List<Row> rows, List<byte> extra, List<Node> nodes)
    {
        _prefix = prefix;
        _rows = rows;
        _extra = extra;
        _nodes = nodes;
        BuildCodes(0, 0, 0);
    }

    public int Count => _rows.Count;

    /// <summary>
    /// Where the string pool ends -- the offset (counted from the start of the pool, tree included) the game stops
    /// reading at. The language table stores it in the 4 bytes at table-header +16, which the format notes call
    /// "reserved zero" but which equals the end of the last real string in every shipped file. Anything stored past
    /// it is invisible to the game (it reads zeros, which decode as "aaaa..."), so it must be moved forward whenever
    /// strings are appended.
    /// </summary>
    public int PoolLength => _poolEnd;

    /// <summary>True when the file was loaded with strings stored past its declared pool end (written by an earlier version of this tool); saving repairs it.</summary>
    public bool NeedsPoolEndRepair { get; private set; }

    /// <summary>The key hash the game uses: CRC-32 (reflected, init 0) over each byte masked with 0xDF.</summary>
    public static uint HashKey(string key)
    {
        uint crc = 0;
        foreach (byte raw in Encoding.UTF8.GetBytes(key))
        {
            crc ^= (byte)(raw & 0xDF);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }

    public static Result<TdbLanguageFile> Parse(byte[] data)
    {
        try
        {
            Result<TdbFile> parsed = TdbFile.Parse(data);
            if (!parsed.IsSuccess)
            {
                return Result.Failure<TdbLanguageFile>(parsed.Error!);
            }

            TdbFile file = parsed.Value!;
            if (file.Tables.Count != 1)
            {
                return Result.Failure<TdbLanguageFile>("Not a language database (expected exactly one table).");
            }

            TdbTable table = file.Tables[0];
            if (table.AllocationType != 66 || table.RecordBytes != RecordSize || table.Fields.Count != 3
                || table.Fields[0].Kind != TdbFieldKind.VarChar1 || table.Fields[0].BitOffset != 0
                || table.Fields[1].Kind != TdbFieldKind.VarChar2 || table.Fields[1].BitOffset != 32
                || table.Fields[2].Kind != TdbFieldKind.UnsignedInt || table.Fields[2].BitOffset != 64 || table.Fields[2].BitWidth != 32)
            {
                return Result.Failure<TdbLanguageFile>("Table layout isn't the language-string layout this writer understands.");
            }

            int recordsEnd = table.DataStart + (RecordSize * table.RowCount);
            byte[] prefix = data.AsSpan(0, table.DataStart).ToArray();
            var rows = new List<Row>(table.RowCount);
            for (int i = 0; i < table.RowCount; i++)
            {
                rows.Add(new Row { Raw = data.AsSpan(table.DataStart + (i * RecordSize), RecordSize).ToArray() });
            }

            var extra = new List<byte>(data.AsSpan(recordsEnd, data.Length - 4 - recordsEnd).ToArray());
            List<Node>? nodes = ParseTree(extra);
            if (nodes is null)
            {
                return Result.Failure<TdbLanguageFile>("The string pool's Huffman tree is malformed.");
            }

            var result = new TdbLanguageFile(prefix, rows, extra, nodes);
            result.InitializePoolEnd(table.HeaderOffset);
            for (int i = 1; i < rows.Count; i++)
            {
                if (rows[i - 1].Hash >= rows[i].Hash)
                {
                    return Result.Failure<TdbLanguageFile>("Rows aren't sorted by hash as expected.");
                }
            }

            return Result.Success(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<TdbLanguageFile>($"Failed to parse language database: {ex.Message}", ex);
        }
    }

    /// <summary>Level-order tree of [index, value] byte pairs; index 0 = leaf. Enqueues RIGHT child before LEFT (spec section 3.1).</summary>
    private static List<Node>? ParseTree(List<byte> extra)
    {
        var nodes = new List<Node> { new() };
        var queue = new Queue<int>();
        queue.Enqueue(0);
        int pos = 0;
        while (queue.Count > 0)
        {
            if (pos + 4 > extra.Count)
            {
                return null;
            }

            int parent = queue.Dequeue();
            int leftIndex = extra[pos];
            int leftValue = extra[pos + 1];
            int rightIndex = extra[pos + 2];
            int rightValue = extra[pos + 3];
            pos += 4;

            nodes.Add(new Node { Leaf = leftIndex == 0 ? leftValue : -1 });
            nodes.Add(new Node { Leaf = rightIndex == 0 ? rightValue : -1 });
            int leftNode = nodes.Count - 2;
            int rightNode = nodes.Count - 1;
            nodes[parent].Left = leftNode;
            nodes[parent].Right = rightNode;

            if (rightIndex != 0)
            {
                queue.Enqueue(rightNode);
            }

            if (leftIndex != 0)
            {
                queue.Enqueue(leftNode);
            }

            if (nodes.Count > 4096)
            {
                return null;
            }
        }

        return nodes;
    }

    private void BuildCodes(int node, uint bits, int length)
    {
        Node n = _nodes[node];
        if (n.Left < 0)
        {
            if (n.Leaf >= 0 && length > 0 && length <= 32)
            {
                _codes[(byte)n.Leaf] = (bits, length);
            }

            return;
        }

        BuildCodes(n.Left, bits << 1, length + 1);
        BuildCodes(n.Right, (bits << 1) | 1, length + 1);
    }

    /// <summary>Offset value the game uses for "no string here" (33 rows of the real English file have it for their text).</summary>
    private const uint MissingOffset = 0xFFFFFFFF;

    private void InitializePoolEnd(int tableHeaderOffset)
    {
        int declared = (int)BinaryPrimitives.ReadUInt32BigEndian(_prefix.AsSpan(tableHeaderOffset + 16));
        int maxEnd = 0;
        foreach (Row row in _rows)
        {
            maxEnd = Math.Max(maxEnd, ExtentEnd(row.KeyOffset, 1));
            maxEnd = Math.Max(maxEnd, ExtentEnd(row.TextOffset, 2));
        }

        _tableHeaderOffset = tableHeaderOffset;
        _poolEnd = Math.Max(declared, maxEnd);
        NeedsPoolEndRepair = declared < maxEnd;
    }

    private int _tableHeaderOffset;

    /// <summary>Offset just past the string at <paramref name="offset"/> (0 for a missing/undecodable one).</summary>
    private int ExtentEnd(uint offset, int prefixBytes)
    {
        if (offset == MissingOffset || offset + prefixBytes > _extra.Count)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < prefixBytes; i++)
        {
            count = (count << 8) | _extra[(int)offset + i];
        }

        long start = offset + prefixBytes;
        int node = 0;
        long bit = 0;
        int produced = 0;
        while (produced < count)
        {
            long idx = start + (bit >> 3);
            if (idx >= _extra.Count)
            {
                return 0;
            }

            int b = (_extra[(int)idx] >> (7 - (int)(bit & 7))) & 1;
            bit++;
            node = b == 0 ? _nodes[node].Left : _nodes[node].Right;
            if (node < 0)
            {
                return 0;
            }

            if (_nodes[node].Left < 0 && _nodes[node].Leaf >= 0)
            {
                produced++;
                node = 0;
            }
        }

        return (int)(start + ((bit + 7) >> 3));
    }

    /// <summary>Writes <paramref name="encoded"/> at the pool end (over the zero padding there) and moves the pool end past it; returns where it went, or -1 if that spot isn't free padding.</summary>
    private int AppendToPool(byte[] encoded)
    {
        int position = _poolEnd;
        for (int i = position; i < Math.Min(_extra.Count, position + encoded.Length); i++)
        {
            if (_extra[i] != 0)
            {
                return -1;
            }
        }

        while (_extra.Count < position + encoded.Length)
        {
            _extra.Add(0);
        }

        for (int i = 0; i < encoded.Length; i++)
        {
            _extra[position + i] = encoded[i];
        }

        _poolEnd = position + encoded.Length;
        return position;
    }

    private byte[]? Decode(uint offset, int prefixBytes)
    {
        if (offset == MissingOffset)
        {
            return Array.Empty<byte>();
        }

        long p = offset;
        if (p + prefixBytes > _extra.Count)
        {
            return null;
        }

        int count = 0;
        for (int i = 0; i < prefixBytes; i++)
        {
            count = (count << 8) | _extra[(int)p + i];
        }

        p += prefixBytes;
        var output = new byte[count];
        int node = 0;
        long bit = 0;
        int produced = 0;
        while (produced < count)
        {
            long idx = p + (bit >> 3);
            if (idx >= _extra.Count)
            {
                return null;
            }

            int b = (_extra[(int)idx] >> (7 - (int)(bit & 7))) & 1;
            bit++;
            node = b == 0 ? _nodes[node].Left : _nodes[node].Right;
            if (node < 0)
            {
                return null;
            }

            if (_nodes[node].Left < 0 && _nodes[node].Leaf >= 0)
            {
                output[produced++] = (byte)_nodes[node].Leaf;
                node = 0;
            }
        }

        return output;
    }

    private bool TryEncode(byte[] bytes, int prefixBytes, out byte[] encoded, out string? error)
    {
        encoded = Array.Empty<byte>();
        error = null;
        if (bytes.Length >= 1 << (8 * prefixBytes))
        {
            error = $"String is {bytes.Length} byte(s); the length prefix only allows {(1 << (8 * prefixBytes)) - 1}.";
            return false;
        }

        var result = new List<byte>(prefixBytes + bytes.Length);
        for (int i = prefixBytes - 1; i >= 0; i--)
        {
            result.Add((byte)(bytes.Length >> (8 * i)));
        }

        byte current = 0;
        int used = 0;
        foreach (byte b in bytes)
        {
            if (!_codes.TryGetValue(b, out (uint Bits, int Length) code))
            {
                string shown = b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"0x{b:X2}";
                error = $"This file's string table has no code for '{shown}', so that character can't be stored (new characters would require rebuilding the whole table).";
                return false;
            }

            for (int i = code.Length - 1; i >= 0; i--)
            {
                current = (byte)((current << 1) | (int)((code.Bits >> i) & 1u));
                if (++used == 8)
                {
                    result.Add(current);
                    current = 0;
                    used = 0;
                }
            }
        }

        if (used > 0)
        {
            result.Add((byte)(current << (8 - used)));
        }

        encoded = result.ToArray();
        return true;
    }

    private int FindRow(uint hash)
    {
        int lo = 0;
        int hi = _rows.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            uint h = _rows[mid].Hash;
            if (h == hash)
            {
                return mid;
            }

            if (h < hash)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    public bool ContainsKey(string key) => FindRow(HashKey(key)) >= 0;

    /// <summary>Null if <paramref name="text"/> can be stored in this file's string pool, otherwise the reason (e.g. a character with no Huffman code).</summary>
    public string? CheckEncodable(string text)
    {
        TryEncode(Encoding.UTF8.GetBytes(text), 2, out _, out string? error);
        return error;
    }

    public bool TryGetText(string key, out string text)
    {
        text = string.Empty;
        int row = FindRow(HashKey(key));
        if (row < 0)
        {
            return false;
        }

        byte[]? bytes = Decode(_rows[row].TextOffset, 2);
        if (bytes is null)
        {
            return false;
        }

        text = Encoding.UTF8.GetString(bytes);
        return true;
    }

    public IEnumerable<(string Key, string Text)> Entries()
    {
        foreach (Row row in _rows)
        {
            byte[]? k = Decode(row.KeyOffset, 1);
            byte[]? t = Decode(row.TextOffset, 2);
            if (k is not null && t is not null)
            {
                yield return (Encoding.UTF8.GetString(k), Encoding.UTF8.GetString(t));
            }
        }
    }

    /// <summary>
    /// Sets (or adds) one string. Null on success, otherwise why it can't be stored. Nothing is
    /// changed on failure.
    /// </summary>
    public string? TrySetText(string key, string text)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => c is < ' ' or > '~'))
        {
            return "Keys must be 1+ printable ASCII characters.";
        }

        uint hash = HashKey(key);
        int index = FindRow(hash);

        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        if (!TryEncode(textBytes, 2, out byte[] encodedText, out string? error))
        {
            return error;
        }

        if (index >= 0)
        {
            byte[]? existingKey = Decode(_rows[index].KeyOffset, 1);
            if (existingKey is null || !string.Equals(Encoding.UTF8.GetString(existingKey), key, StringComparison.OrdinalIgnoreCase))
            {
                return $"Key '{key}' collides with a different existing key.";
            }

            int placed = AppendToPool(encodedText);
            if (placed < 0)
            {
                return "The free space at the end of this file's string pool isn't empty, so a new string can't be added safely.";
            }

            _rows[index].TextOffset = (uint)placed;
            return null;
        }

        if (_rows.Count >= ushort.MaxValue)
        {
            return "The language table is full.";
        }

        if (!TryEncode(Encoding.UTF8.GetBytes(key), 1, out byte[] encodedKey, out error))
        {
            return error;
        }

        var raw = new byte[RecordSize];
        var row = new Row { Raw = raw };
        int keyAt = AppendToPool(encodedKey);
        int textAt = keyAt < 0 ? -1 : AppendToPool(encodedText);
        if (keyAt < 0 || textAt < 0)
        {
            if (keyAt >= 0)
            {
                _poolEnd = keyAt; // undo the key placement; the bytes left behind are unreferenced
            }

            return "The free space at the end of this file's string pool isn't empty, so a new string can't be added safely.";
        }

        row.KeyOffset = (uint)keyAt;
        row.TextOffset = (uint)textAt;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(_hashFieldRecordOffset), hash);
        _rows.Insert(~index, row);
        return null;
    }

    /// <summary>The whole file, rebuilt with the new row count/size and every checksum recomputed.</summary>
    public byte[] ToBytes()
    {
        int recordsLength = _rows.Count * RecordSize;
        var buffer = new byte[_prefix.Length + recordsLength + _extra.Count + 4];
        _prefix.CopyTo(buffer, 0);
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Raw.CopyTo(buffer, _prefix.Length + (i * RecordSize));
        }

        _extra.CopyTo(buffer, _prefix.Length + recordsLength);

        // Patch the sizes the structure carries: file size (header +8) and the table's max/current record counts.
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), (uint)buffer.Length);
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(28));
        int hp = 24 + 8 + (int)tableOffset;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(hp + 20), (ushort)_rows.Count);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(hp + 22), (ushort)_rows.Count);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(hp + 16), (uint)_poolEnd);

        Result<TdbFile> reparsed = TdbFile.Parse(buffer);
        if (!reparsed.IsSuccess)
        {
            throw new InvalidOperationException($"Rebuilt language database didn't re-parse: {reparsed.Error}");
        }

        return reparsed.Value!.ToBytes();
    }
}
