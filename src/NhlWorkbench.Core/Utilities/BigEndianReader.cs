using System.Buffers.Binary;
using System.Text;

namespace NhlWorkbench.Core.Utilities;

/// <summary>
/// Cursor-based big-endian reader over an in-memory byte array. All NHL Legacy
/// container formats (RX2, RPSGL, BIGF) store multi-byte fields big-endian, so this
/// is the single place that convention is expressed.
/// </summary>
public sealed class BigEndianReader
{
    private readonly byte[] _data;

    public BigEndianReader(byte[] data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public int Length => _data.Length;

    public bool CanRead(int byteCount) => Position + byteCount <= _data.Length;

    public byte ReadByte() => _data[Position++];

    public byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        Array.Copy(_data, Position, result, 0, count);
        Position += count;
        return result;
    }

    public ushort ReadUInt16BE()
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public short ReadInt16BE()
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public uint ReadUInt32BE()
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public int ReadInt32BE()
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    /// <summary>Reads a null-terminated ASCII string starting at the current position and consumes the terminator.</summary>
    public string ReadNullTerminatedAscii()
    {
        int start = Position;
        while (Position < _data.Length && _data[Position] != 0)
        {
            Position++;
        }

        string value = Encoding.ASCII.GetString(_data, start, Position - start);
        if (Position < _data.Length)
        {
            Position++; // consume the terminator
        }

        return value;
    }

    public void Seek(int position) => Position = position;

    public void Advance(int count) => Position += count;

    // Static helpers for one-off reads at a known absolute offset, without a cursor.
    public static byte ReadByteAt(byte[] data, int offset) => data[offset];

    public static ushort ReadUInt16BEAt(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    public static short ReadInt16BEAt(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));

    public static uint ReadUInt32BEAt(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    public static int ReadInt32BEAt(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));

    public static float ReadSingleBEAt(byte[] data, int offset) =>
        BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4));

    public static string ReadNullTerminatedAsciiAt(byte[] data, int offset)
    {
        int pos = offset;
        while (pos < data.Length && data[pos] != 0)
        {
            pos++;
        }

        return Encoding.ASCII.GetString(data, offset, pos - offset);
    }
}
