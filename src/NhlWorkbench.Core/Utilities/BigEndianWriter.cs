using System.Buffers.Binary;
using System.Text;

namespace NhlWorkbench.Core.Utilities;

/// <summary>
/// Growable big-endian writer used to serialize RX2/RPSGL/BIGF containers. Backed by
/// a <see cref="MemoryStream"/> so callers can seek backward and patch header fields
/// (offsets/sizes) that are only known after the content they describe has been laid out.
/// </summary>
public sealed class BigEndianWriter
{
    private readonly MemoryStream _stream = new();

    public int Position
    {
        get => (int)_stream.Position;
        set => _stream.Position = value;
    }

    public int Length => (int)_stream.Length;

    public void WriteByte(byte value) => _stream.WriteByte(value);

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteUInt16BE(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteInt16BE(short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteUInt32BE(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteInt32BE(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteNullTerminatedAscii(string value)
    {
        _stream.Write(Encoding.ASCII.GetBytes(value));
        _stream.WriteByte(0);
    }

    /// <summary>Pads with <paramref name="fillByte"/> until the current length is a multiple of <paramref name="alignment"/>.</summary>
    public void PadTo(int alignment, byte fillByte = 0)
    {
        long remainder = _stream.Length % alignment;
        if (remainder == 0)
        {
            return;
        }

        long padCount = alignment - remainder;
        _stream.Position = _stream.Length;
        for (long i = 0; i < padCount; i++)
        {
            _stream.WriteByte(fillByte);
        }
    }

    /// <summary>Overwrites bytes at an already-written offset (e.g. patching a size field after the fact) without disturbing the append position.</summary>
    public void WriteAt(int offset, ReadOnlySpan<byte> bytes)
    {
        long saved = _stream.Position;
        _stream.Position = offset;
        _stream.Write(bytes);
        _stream.Position = saved;
    }

    public void WriteUInt32BEAt(int offset, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        WriteAt(offset, buffer);
    }

    public void WriteInt32BEAt(int offset, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        WriteAt(offset, buffer);
    }

    public void WriteUInt16BEAt(int offset, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        WriteAt(offset, buffer);
    }

    /// <summary>BIGF's one confirmed-little-endian field (the top-level archive-size at byte
    /// offset 4) -- every other multi-byte field in every format this writer serializes is
    /// big-endian, so this exists only for that one documented exception.</summary>
    public void WriteUInt32LEAt(int offset, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        WriteAt(offset, buffer);
    }

    public void SeekToEnd() => _stream.Position = _stream.Length;

    public byte[] ToArray() => _stream.ToArray();
}
