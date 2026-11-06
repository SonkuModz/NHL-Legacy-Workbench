using System.Buffers.Binary;
using System.IO.Compression;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Compression;

/// <summary>
/// EA "chunkzip", the chunked deflate wrapper around most payloads in EB archives.
///
/// All integers are big-endian. The first u32 after the 8-byte tag selects the header:
/// <code>
/// value &lt;= 2 (newer):  version, fullSize, chunkSize, chunkCount, alignment; chunks start at 0x28.
///                      Each chunk: padding so its data lands on the alignment, u32 size, u32 flag, data.
///                      Flag 1 = raw deflate, 4 = stored.
/// value &gt; 2 (older):   the value is fullSize, then chunkSize; chunks start at 0x10.
///                      Each chunk: u32 size, data. Chunk count is ceil(fullSize / chunkSize).
/// </code>
/// Layout from Modding Studio's tdb-archive-codec and the PS3 recomp's tools/chunkzip.py.
/// </summary>
public static class ChunkZip
{
    /// <summary>Largest decoded payload this will allocate.</summary>
    public const int MaxDecodedSize = 1 << 30;

    private const int FlagDeflate = 1;
    private const int FlagStored = 4;
    private const int NewerHeaderLength = 0x28;
    private const int OlderHeaderLength = 0x10;
    private const int DefaultAlignment = 16;

    /// <summary>Deflate can't expand beyond roughly 1032:1, so a larger claim means a corrupt header.</summary>
    private const long MaxDeflateRatio = 1040;

    private static ReadOnlySpan<byte> Magic => "chunkzip"u8;

    public static bool IsChunkZip(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    public static Result<byte[]> Decompress(byte[] input)
    {
        if (input.Length < 16 || !IsChunkZip(input))
        {
            return Result.Failure<byte[]>("Not a chunkzip payload.");
        }

        try
        {
            uint first = ReadU32(input, 8);
            return first <= 2 ? DecompressNewer(input) : DecompressOlder(input, first);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return Result.Failure<byte[]>($"Corrupt chunkzip payload: {ex.Message}", ex);
        }
    }

    private static Result<byte[]> DecompressNewer(byte[] input)
    {
        if (input.Length < NewerHeaderLength)
        {
            return Result.Failure<byte[]>("Chunkzip header is truncated.");
        }

        uint fullSize = ReadU32(input, 12);
        uint chunkSize = ReadU32(input, 16);
        uint chunkCount = ReadU32(input, 20);
        uint alignment = ReadU32(input, 24);
        if (alignment == 0)
        {
            alignment = DefaultAlignment;
        }

        if (alignment > 1 << 20 || (alignment & (alignment - 1)) != 0)
        {
            return Result.Failure<byte[]>($"Chunkzip alignment {alignment} is not a usable power of two.");
        }

        string? invalid = ValidateSizes(input, fullSize, chunkSize, chunkCount);
        if (invalid is not null)
        {
            return Result.Failure<byte[]>(invalid);
        }

        var output = new byte[fullSize];
        int written = 0;
        long position = NewerHeaderLength;
        for (uint i = 0; i < chunkCount; i++)
        {
            // The 8-byte chunk header sits immediately before an aligned data start.
            long dataStart = (position + 8 + alignment - 1) / alignment * alignment;
            position = dataStart - 8;
            if (position + 8 > input.Length)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount} starts past the end of the payload.");
            }

            uint compressedSize = ReadU32(input, (int)position);
            uint flag = ReadU32(input, (int)position + 4);
            if (compressedSize > input.Length - dataStart)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount} runs past the end of the payload.");
            }

            string? error = DecodeChunk(input, (int)dataStart, (int)compressedSize, flag, output, ref written);
            if (error is not null)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount}: {error}");
            }

            position = dataStart + compressedSize;
        }

        return Finish(output, written);
    }

    private static Result<byte[]> DecompressOlder(byte[] input, uint fullSize)
    {
        uint chunkSize = ReadU32(input, 12);
        uint chunkCount = chunkSize == 0 ? 0 : (uint)(((ulong)fullSize + chunkSize - 1) / chunkSize);
        string? invalid = ValidateSizes(input, fullSize, chunkSize, chunkCount);
        if (invalid is not null)
        {
            return Result.Failure<byte[]>(invalid);
        }

        var output = new byte[fullSize];
        int written = 0;
        long position = OlderHeaderLength;
        for (uint i = 0; i < chunkCount; i++)
        {
            if (position + 4 > input.Length)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount} starts past the end of the payload.");
            }

            uint compressedSize = ReadU32(input, (int)position);
            position += 4;
            if (compressedSize > input.Length - position)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount} runs past the end of the payload.");
            }

            string? error = DecodeChunk(input, (int)position, (int)compressedSize, FlagDeflate, output, ref written);
            if (error is not null)
            {
                return Result.Failure<byte[]>($"Chunk {i} of {chunkCount}: {error}");
            }

            position += compressedSize;
        }

        return Finish(output, written);
    }

    private static string? ValidateSizes(byte[] input, uint fullSize, uint chunkSize, uint chunkCount)
    {
        if (fullSize > MaxDecodedSize)
        {
            return $"Decoded size {fullSize:N0} is over the {MaxDecodedSize:N0} byte limit.";
        }

        if (fullSize > input.Length * MaxDeflateRatio)
        {
            return $"Decoded size {fullSize:N0} is impossible for a {input.Length:N0} byte payload.";
        }

        if (fullSize > 0 && chunkSize == 0)
        {
            return "Chunk size is zero.";
        }

        // Every chunk needs at least its size field, so the count can't exceed the payload.
        if (chunkCount > input.Length / 4)
        {
            return $"Chunk count {chunkCount:N0} is impossible for a {input.Length:N0} byte payload.";
        }

        return null;
    }

    private static string? DecodeChunk(byte[] input, int start, int length, uint flag, byte[] output, ref int written)
    {
        int remaining = output.Length - written;
        if (flag == FlagStored)
        {
            if (length > remaining)
            {
                return "stored bytes are larger than the declared size.";
            }

            Array.Copy(input, start, output, written, length);
            written += length;
            return null;
        }

        // Most chunks are raw deflate; Modding Studio also found zlib-wrapped ones.
        if (TryInflate(new DeflateStream(new MemoryStream(input, start, length), CompressionMode.Decompress), output, written, out int produced, out bool overflow)
            || TryInflate(new ZLibStream(new MemoryStream(input, start, length), CompressionMode.Decompress), output, written, out produced, out overflow))
        {
            written += produced;
            return null;
        }

        return overflow ? "inflates past the declared size." : $"can't be inflated (flag {flag}).";
    }

    private static bool TryInflate(Stream stream, byte[] output, int offset, out int produced, out bool overflow)
    {
        produced = 0;
        overflow = false;
        using (stream)
        {
            try
            {
                int read;
                while (offset + produced < output.Length
                    && (read = stream.Read(output, offset + produced, output.Length - offset - produced)) > 0)
                {
                    produced += read;
                }

                if (offset + produced == output.Length && stream.ReadByte() != -1)
                {
                    overflow = true;
                    return false;
                }

                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }

    private static Result<byte[]> Finish(byte[] output, int written) =>
        written == output.Length
            ? Result.Success(output)
            : Result.Failure<byte[]>($"Chunks produced {written:N0} bytes but the header declares {output.Length:N0}.");

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
