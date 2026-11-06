using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>Audio held in the bank itself: where a run of codec bytes sits, and how many samples it declares.</summary>
public sealed record AbkSegment(int Offset, int Length, int Samples);

/// <summary>One sound in a bank's S10A table, with the name the game calls it by if the bank's directory has one.</summary>
public sealed class AbkRecord
{
    public required int Index { get; init; }

    /// <summary>Where its 8-byte header starts in the bank.</summary>
    public required int Offset { get; init; }

    /// <summary>One past its last byte in the bank.</summary>
    public required int End { get; init; }

    public required int CodecId { get; init; }

    /// <summary>0 mono, 1 stereo, 3 two stereo streams, 5 three.</summary>
    public required int ChannelConfig { get; init; }

    public required int SampleRate { get; init; }

    public required int SampleCount { get; init; }

    /// <summary>The audio is in the sibling .ast file rather than in the bank.</summary>
    public required bool IsStream { get; init; }

    public required bool IsLooped { get; init; }

    /// <summary>For a looped sound in the bank, the sample it loops back to.</summary>
    public int? LoopStart { get; init; }

    /// <summary>For a looped stream, the samples and size of its short lead-in block.</summary>
    public (int Samples, int Size)? LoopStub { get; init; }

    public IReadOnlyList<AbkSegment> Segments { get; init; } = [];

    public string Name { get; internal set; } = string.Empty;

    /// <summary>Where a stream's audio starts in the .ast file.</summary>
    public long? StreamOffset { get; internal set; }

    public int Channels => ChannelConfig + 1;

    public double Seconds => SampleRate > 0 ? SampleCount / (double)SampleRate : 0;
}

/// <summary>
/// A PS3 ABKC sound bank read all the way through: its table of sounds, including the ones streamed from an .ast file,
/// and the names in its directory. The layout is the one the modding community measured; the header words are:
/// 0x14 file size, 0x18 table offset, 0x1C tree start, 0x24 table data size, 0x2C .ast id, then three tail sections at
/// 0x30, 0x34 (pointers into the tree) and 0x38 (the name directory).
/// </summary>
public sealed class AbkBank
{
    private const uint Unused = 0xFFFFFFFF;

    private AbkBank(byte[] data, int slots, IReadOnlyList<AbkRecord> records)
    {
        Data = data;
        SlotCount = slots;
        Records = records;
    }

    public byte[] Data { get; }

    /// <summary>How many slots the table has, including empty ones.</summary>
    public int SlotCount { get; }

    /// <summary>The slots with a sound in them.</summary>
    public IReadOnlyList<AbkRecord> Records { get; }

    public bool HasStreams => Records.Any(r => r.IsStream);

    public uint AstId => Be32(Data, 0x2C);

    private int TableOffset => (int)Be32(Data, 0x18);

    public static bool IsBank(byte[] data) => data.Length >= 0x40 && data[0] == 'A' && data[1] == 'B' && data[2] == 'K' && data[3] == 'C';

    public static Result<AbkBank> Parse(byte[] data)
    {
        try
        {
            if (!IsBank(data))
            {
                return Result.Failure<AbkBank>("This isn't an ABKC sound bank.");
            }

            if (Be32(data, 0x14) != data.Length)
            {
                return Result.Failure<AbkBank>("The bank's size doesn't match its header, so it may be damaged.");
            }

            int table = (int)Be32(data, 0x18);
            uint dataSize = Be32(data, 0x24);
            long dataEnd = table + (long)dataSize;
            if (table + 16L > data.Length || dataEnd > data.Length || data[table] != 'S' || data[table + 1] != '1' || data[table + 2] != '0' || data[table + 3] != 'A')
            {
                return Result.Failure<AbkBank>("The bank's sound table wasn't found where its header says.");
            }

            int slots = (int)Be32(data, table + 8);
            if (slots < 0 || table + 12L + (slots * 4L) > dataEnd)
            {
                return Result.Failure<AbkBank>("The bank's sound table is damaged.");
            }

            var records = new List<AbkRecord>(slots);
            for (int i = 0; i < slots; i++)
            {
                uint at = Be32(data, table + 12 + (i * 4));
                if (at == Unused)
                {
                    continue;
                }

                if (table + (long)at + 8 > dataEnd)
                {
                    return Result.Failure<AbkBank>($"Sound {i} starts outside the bank's table.");
                }

                Result<AbkRecord> record = ReadRecord(data, i, table + (int)at, (int)dataEnd);
                if (!record.IsSuccess)
                {
                    return Result.Failure<AbkBank>($"Sound {i}: {record.Error}");
                }

                records.Add(record.Value!);
            }

            ReadNames(data, table, records);
            return Result.Success(new AbkBank(data, slots, records));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return Result.Failure<AbkBank>($"The bank couldn't be read: {ex.Message}", ex);
        }
    }

    private static Result<AbkRecord> ReadRecord(byte[] data, int index, int at, int dataEnd)
    {
        uint first = Be32(data, at);
        uint second = Be32(data, at + 4);
        int streamType = (int)(second >> 30);
        bool looped = ((second >> 29) & 1) == 1;
        int samples = (int)(second & 0x1FFFFFFF);
        int cursor = at + 8;
        int? loopStart = null;
        (int, int)? stub = null;
        var segments = new List<AbkSegment>();

        if (streamType == 1)
        {
            if (looped)
            {
                stub = ((int)Be32(data, cursor), (int)Be32(data, cursor + 4));
                cursor += 8;
            }
        }
        else if (streamType == 0)
        {
            if (looped)
            {
                loopStart = (int)Be32(data, cursor);
                cursor += 4;
            }

            long sum = 0;
            do
            {
                if (cursor + 8 > dataEnd)
                {
                    return Result.Failure<AbkRecord>("its audio runs past the end of the table.");
                }

                uint size = Be32(data, cursor);
                uint count = Be32(data, cursor + 4);
                if (size < 8 || cursor + (long)size > dataEnd)
                {
                    return Result.Failure<AbkRecord>("its audio has a bad size.");
                }

                segments.Add(new AbkSegment(cursor + 8, (int)size - 8, (int)count));
                sum += count;
                cursor += (int)size;
            }
            while (looped && sum < samples && segments.Count < 2);

            if (sum != samples)
            {
                return Result.Failure<AbkRecord>("its audio holds a different number of samples than its header says.");
            }
        }
        else
        {
            return Result.Failure<AbkRecord>($"it has an unknown storage type ({streamType}).");
        }

        return Result.Success(new AbkRecord
        {
            Index = index,
            Offset = at,
            End = cursor,
            CodecId = (int)((first >> 24) & 0xF),
            ChannelConfig = (int)((first >> 18) & 0x3F),
            SampleRate = (int)(first & 0x3FFFF),
            SampleCount = samples,
            IsStream = streamType == 1,
            IsLooped = looped,
            LoopStart = loopStart,
            LoopStub = stub,
            Segments = segments,
        });
    }

    /// <summary>
    /// Names come from the tree: each pointer in the second tail section leads to a list of the sounds a named node can
    /// play. A bank whose tree doesn't read this way still works, its sounds just go unnamed.
    /// </summary>
    private static void ReadNames(byte[] data, int table, List<AbkRecord> records)
    {
        try
        {
            int tree = (int)Be32(data, 0x1C);
            int pointers = (int)Be32(data, 0x34);
            int directory = (int)Be32(data, 0x38);
            int slotOffset = data[8] == 0x0A ? 4 : 0;

            int nameCount = (int)Be32(data, directory);
            var names = new List<(int Node, string Name)>(nameCount);
            for (int i = 0; i < nameCount; i++)
            {
                int entry = directory + 4 + (12 * i);
                int node = (int)Be32(data, entry);
                int nameAt = (int)Be32(data, entry + 4) + 4;
                int end = Array.IndexOf(data, (byte)0, nameAt);
                names.Add((node, System.Text.Encoding.ASCII.GetString(data, nameAt, (end < 0 ? data.Length : end) - nameAt)));
            }

            names.Sort((a, b) => a.Node.CompareTo(b.Node));
            var byIndex = records.ToDictionary(r => r.Index);
            int pointerCount = (int)Be32(data, pointers);
            var seen = new HashSet<int>();
            for (int i = 0; i < pointerCount; i++)
            {
                int slot = (int)Be32(data, pointers + 4 + (4 * i));
                int list = (int)Be32(data, slot + slotOffset);
                if (list < tree || list >= table || data[list] != 0 || !seen.Add(list))
                {
                    continue;
                }

                int count = (int)Be32(data, list);
                if (count <= 0 || list + 4L + (12L * count) > table)
                {
                    continue;
                }

                string owner = string.Empty;
                foreach ((int node, string name) in names)
                {
                    if (node > slot)
                    {
                        break;
                    }

                    owner = name;
                }

                int position = 0;
                for (int k = 0; k < count; k++)
                {
                    int entry = list + 4 + (12 * k);
                    int index = (data[entry] << 8) | data[entry + 1];
                    if (index == 0xFFFF || !byIndex.TryGetValue(index, out AbkRecord? record))
                    {
                        continue;
                    }

                    position++;
                    if (record.Name.Length == 0)
                    {
                        record.Name = count > 1 && owner.Length > 0 ? $"{owner} {position}" : owner;
                    }

                    uint streamAt = Be32(data, entry + 8);
                    if (streamAt != Unused && record.StreamOffset is null)
                    {
                        record.StreamOffset = streamAt;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // The sounds still play. They just have no names.
        }
    }

    /// <summary>
    /// A copy of the bank with one sound's audio swapped for other audio of the same kind, which may be longer or shorter.
    /// Everything after it moves along: later sounds, then the tail sections, whose offsets are all corrected.
    /// </summary>
    public Result<byte[]> Replace(AbkRecord record, byte[] payload, int samples)
    {
        if (record.IsStream)
        {
            return Result.Failure<byte[]>("This sound is streamed from an .ast file, so it can't be swapped inside the bank.");
        }

        int headerExtra = record.IsLooped ? 4 : 0;
        var region = new byte[8 + headerExtra + 8 + payload.Length];
        WriteBe32(region, 0, Be32(Data, record.Offset));
        WriteBe32(region, 4, (Be32(Data, record.Offset + 4) & 0xE0000000) | (uint)samples);
        if (record.IsLooped)
        {
            // The whole of the new sound loops, since the old loop point means nothing to it.
            WriteBe32(region, 8, 0);
        }

        WriteBe32(region, 8 + headerExtra, (uint)(8 + payload.Length));
        WriteBe32(region, 12 + headerExtra, (uint)samples);
        Array.Copy(payload, 0, region, 16 + headerExtra, payload.Length);

        // Sounds sit edge to edge, then up to 3 bytes pad the table so the sections after it start on a 4-byte boundary.
        int table = TableOffset;
        int dataEnd = table + (int)Be32(Data, 0x24);
        int lastEnd = Records.Max(r => r.End);
        int soundDelta = region.Length - (record.End - record.Offset);
        int newLastEnd = lastEnd + soundDelta;
        int newDataEnd = (newLastEnd + 3) & ~3;
        int tailDelta = newDataEnd - dataEnd;

        var result = new byte[Data.Length + tailDelta];
        Array.Copy(Data, 0, result, 0, record.Offset);
        Array.Copy(region, 0, result, record.Offset, region.Length);
        Array.Copy(Data, record.End, result, record.Offset + region.Length, lastEnd - record.End);
        Array.Copy(Data, dataEnd, result, newDataEnd, Data.Length - dataEnd);

        WriteBe32(result, 0x14, (uint)result.Length);
        WriteBe32(result, 0x24, (uint)(newDataEnd - table));
        foreach (int word in new[] { 0x30, 0x34, 0x38 })
        {
            WriteBe32(result, word, Be32(Data, word) + (uint)tailDelta);
        }

        for (int i = 0; i < SlotCount; i++)
        {
            int slot = table + 12 + (i * 4);
            uint at = Be32(result, slot);
            if (at != Unused && table + at > record.Offset)
            {
                WriteBe32(result, slot, at + (uint)soundDelta);
            }
        }

        // Directory entries point at their names, which sit after it.
        int directory = (int)Be32(result, 0x38);
        int names = (int)Be32(result, directory);
        for (int i = 0; i < names; i++)
        {
            int entry = directory + 4 + (12 * i) + 4;
            WriteBe32(result, entry, Be32(result, entry) + (uint)tailDelta);
        }

        return Parse(result).IsSuccess ? Result.Success(result) : Result.Failure<byte[]>("The rebuilt bank didn't read back correctly, so it wasn't kept.");
    }

    private static uint Be32(byte[] data, int at) =>
        (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);

    private static void WriteBe32(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }
}
