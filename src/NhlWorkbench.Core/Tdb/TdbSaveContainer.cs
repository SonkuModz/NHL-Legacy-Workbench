using System.Buffers.Binary;
using System.IO.Compression;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Tdb;

/// <summary>
/// The wrapper the game puts around a roster save: a 0x30-byte header, then a zlib stream holding a
/// normal TDB database (the "rosters" copy of the team / jersey / equipment tables that the game loads
/// OVER nhlng.db). Layout from the NHL Modding Studio's verified save-container spec and re-checked
/// against the user's real roster files:
/// <code>
/// 0x00 magic "RosterFile" (PS3: "PS3RosterFile")    0x10 checksum_a  BE  CRC-32/IEEE   over file[0x1C..]
/// 0x14 version (4)                                  0x18 flags 0     0x1C 1     0x20 platform marker
/// 0x24 uncompressed size BE                         0x28 checksum_b  BE  CRC-32/BZIP2 over file[0x2C..]
/// 0x2C compressed size  LE                          0x30 zlib stream, then zero padding to the file's allocation
/// </code>
/// Only this "version 4" zlib container is supported (it is what NHL Legacy writes). Rebuilding writes a
/// fresh zlib stream (any valid stream works -- the game's own differs byte-for-byte from every
/// library's output) and pads back to the original file length; it refuses to grow the file.
/// </summary>
public sealed class TdbSaveContainer
{
    private const int HeaderLength = 0x30;

    private TdbSaveContainer(byte[] original, byte[] innerDatabase, byte[] trailer)
    {
        Original = original;
        InnerDatabase = innerDatabase;
        Trailer = trailer;
    }

    public byte[] Original { get; }

    /// <summary>The inflated TDB database inside the save (exactly the database's own declared size).</summary>
    public byte[] InnerDatabase { get; }

    /// <summary>Bytes the save keeps after the database inside the zlib stream (4 in real Xbox/Legacy saves); preserved on rebuild.</summary>
    public byte[] Trailer { get; }

    /// <summary>True when the file looks like a roster/save wrapper (as opposed to a bare "DB" database).</summary>
    public static bool LooksLikeContainer(byte[] data) =>
        data.Length > HeaderLength && !(data[0] == (byte)'D' && data[1] == (byte)'B')
        && (data.AsSpan(0, 16).StartsWith("RosterFile"u8) || data.AsSpan(0, 16).StartsWith("PS3RosterFile"u8));

    public static Result<TdbSaveContainer> Parse(byte[] data)
    {
        try
        {
            if (!LooksLikeContainer(data))
            {
                return Result.Failure<TdbSaveContainer>("Not a roster save (no 'RosterFile' header).");
            }

            uint version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x14));
            if (version != 4 || data[HeaderLength] != 0x78)
            {
                return Result.Failure<TdbSaveContainer>($"Only the zlib save container (version 4) is supported; this one reports version {version}.");
            }

            uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x24));
            if (declaredSize == 0 || declaredSize > 128 * 1024 * 1024)
            {
                return Result.Failure<TdbSaveContainer>("The save's declared uncompressed size isn't plausible.");
            }

            using var input = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            byte[] inner = output.ToArray();

            if (inner.Length < 24 || inner[0] != (byte)'D' || inner[1] != (byte)'B')
            {
                return Result.Failure<TdbSaveContainer>("The data inside the save isn't a TDB database.");
            }

            uint databaseSize = BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(8));
            if (databaseSize < 24 || databaseSize > inner.Length)
            {
                return Result.Failure<TdbSaveContainer>("The database inside the save declares a size larger than its data.");
            }

            return Result.Success(new TdbSaveContainer(data, inner[..(int)databaseSize], inner[(int)databaseSize..]));
        }
        catch (Exception ex)
        {
            return Result.Failure<TdbSaveContainer>($"Couldn't read the save: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The save file's bytes with <paramref name="newInnerDatabase"/> inside: sizes updated, a fresh zlib
    /// stream, zero padding back to the original length, then both checksums. Fails (nothing returned) if the
    /// compressed data no longer fits the original allocation or doesn't round-trip.
    /// </summary>
    public Result<byte[]> Build(byte[] newInnerDatabase)
    {
        try
        {
            byte[] payload = newInnerDatabase.Concat(Trailer).ToArray();
            byte[] stream;
            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(payload);
                }

                stream = compressed.ToArray();
            }

            if (HeaderLength + stream.Length > Original.Length)
            {
                return Result.Failure<byte[]>("The edited save no longer fits the file's original size (it would have to grow), so it wasn't built.");
            }

            var file = new byte[Original.Length];
            Original.AsSpan(0, HeaderLength).CopyTo(file);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x24), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x2C), (uint)stream.Length);
            stream.CopyTo(file, HeaderLength);

            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x28), ChecksumB(file));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x10), ChecksumA(file));

            Result<TdbSaveContainer> check = Parse(file);
            if (!check.IsSuccess || !check.Value!.InnerDatabase.AsSpan().SequenceEqual(newInnerDatabase))
            {
                return Result.Failure<byte[]>("The rebuilt save didn't read back identically, so it wasn't built.");
            }

            return Result.Success(file);
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"Couldn't rebuild the save: {ex.Message}", ex);
        }
    }

    /// <summary>Stored checksums that don't match the file's current bytes (empty = consistent).</summary>
    public static IReadOnlyList<string> VerifyChecksums(byte[] file)
    {
        var problems = new List<string>();
        if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(0x10)) != ChecksumA(file))
        {
            problems.Add("checksum A (0x10)");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(0x28)) != ChecksumB(file))
        {
            problems.Add("checksum B (0x28)");
        }

        return problems;
    }

    /// <summary>CRC-32/IEEE (the zip/zlib CRC) over file[0x1C..].</summary>
    private static uint ChecksumA(byte[] file)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in file.AsSpan(0x1C))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>CRC-32/BZIP2 = the TDB CRC (MPEG-2) with the final complement kept, over file[0x2C..].</summary>
    private static uint ChecksumB(byte[] file) => ~TdbCrc.Compute(file.AsSpan(0x2C));
}
