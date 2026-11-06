using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Eb;

/// <summary>One file listed in an EB archive.</summary>
/// <param name="Index">Row number in the file table.</param>
/// <param name="Path">Folder and name joined with forward slashes, as stored in the archive.</param>
/// <param name="Offset">Absolute byte offset of the stored payload.</param>
/// <param name="StoredSize">Bytes the payload occupies in the archive.</param>
/// <param name="Hash">The row's fourth word. Not a checksum of the payload.</param>
public sealed record EbEntry(int Index, string Path, long Offset, long StoredSize, uint Hash);

/// <summary>
/// Read-only EA <c>EB\0\3</c> archive, the main archive format on the NHL Legacy PS3 disc.
///
/// Only the tables are held in memory. The disc's archives reach 1.7 GB, so payloads are read
/// from the stream on demand. All integers are big-endian:
/// <code>
/// 0x00 "EB" u16 version(3)   0x04 u32 fileCount      0x08 u32 variant    0x0C u32 nameTableOffset
/// 0x14 u8 nameRecordLength   0x15 u8 folderRecordLength   0x16 u16 folderCount   0x18 u64 archiveSize
/// 0x30 file table: u32 offset/16, u32 storedSize (0 = use size), u32 size, u32 hash[, u32 extra]
/// nameTableOffset: per file, u16 folderIndex + NUL-padded name
/// next 16-byte boundary: per folder, NUL-padded path
/// </code>
/// Layout from Modding Studio's tdb-archive-big and the PS3 recomp's tools/eb_extract.py.
/// Reads are serialized, so one instance is safe to share between threads.
/// </summary>
public sealed class EbArchive : IDisposable
{
    /// <summary>Largest table block this will allocate.</summary>
    private const long MaxTableBytes = 256L * 1024 * 1024;

    private const int HeaderLength = 0x20;
    private const int FileTableOffset = 0x30;
    private const int BlockSize = 16;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly object _readLock = new();
    private readonly Dictionary<string, EbEntry> _byPath;

    private EbArchive(string sourceName, Stream stream, bool leaveOpen, uint variant, long declaredSize, IReadOnlyList<EbEntry> entries)
    {
        SourceName = sourceName;
        _stream = stream;
        _leaveOpen = leaveOpen;
        Variant = variant;
        DeclaredSize = declaredSize;
        Entries = entries;

        // Duplicate paths resolve to the first row.
        _byPath = new Dictionary<string, EbEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (EbEntry entry in entries)
        {
            _byPath.TryAdd(entry.Path, entry);
        }
    }

    public string SourceName { get; }

    /// <summary>The header's variant word. It selects the file table's row width.</summary>
    public uint Variant { get; }

    /// <summary>The header's size claim. Some variants legitimately disagree with the real length.</summary>
    public long DeclaredSize { get; }

    public IReadOnlyList<EbEntry> Entries { get; }

    public static bool IsEbArchive(ReadOnlySpan<byte> data) => data.StartsWith("EB\0\u0003"u8);

    /// <summary>Opens a file on disk for reading. The file is never written.</summary>
    public static Result<EbArchive> OpenFile(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<EbArchive>($"Can't open {path}: {ex.Message}", ex);
        }

        Result<EbArchive> result = Open(stream, path);
        if (!result.IsSuccess)
        {
            stream.Dispose();
        }

        return result;
    }

    /// <summary>Parses an archive from a seekable stream. On success the archive owns the stream unless <paramref name="leaveOpen"/> is set.</summary>
    public static Result<EbArchive> Open(Stream stream, string sourceName, bool leaveOpen = false)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            return Result.Failure<EbArchive>("An EB archive needs a readable, seekable stream.");
        }

        try
        {
            return Parse(stream, sourceName, leaveOpen);
        }
        catch (IOException ex)
        {
            return Result.Failure<EbArchive>($"Can't read {sourceName}: {ex.Message}", ex);
        }
    }

    /// <summary>Finds an entry by path, ignoring case.</summary>
    public EbEntry? Find(string path) => _byPath.GetValueOrDefault(path.Replace('\\', '/'));

    /// <summary>Reads the payload exactly as stored in the archive.</summary>
    public Result<byte[]> ReadStored(EbEntry entry)
    {
        if (entry.StoredSize > Array.MaxLength)
        {
            return Result.Failure<byte[]>($"{entry.Path} is too large to read into memory ({entry.StoredSize:N0} bytes).");
        }

        try
        {
            lock (_readLock)
            {
                if (entry.Offset + entry.StoredSize > _stream.Length)
                {
                    return Result.Failure<byte[]>($"{entry.Path} runs past the end of {SourceName}.");
                }

                var data = new byte[entry.StoredSize];
                _stream.Seek(entry.Offset, SeekOrigin.Begin);
                _stream.ReadExactly(data);
                return Result.Success(data);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return Result.Failure<byte[]>($"Can't read {entry.Path}: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the payload and removes its chunkzip wrapper, if it has one.</summary>
    public Result<byte[]> ReadDecoded(EbEntry entry)
    {
        Result<byte[]> stored = ReadStored(entry);
        if (!stored.IsSuccess || !ChunkZip.IsChunkZip(stored.Value))
        {
            return stored;
        }

        Result<byte[]> decoded = ChunkZip.Decompress(stored.Value!);
        return decoded.IsSuccess
            ? decoded
            : Result.Failure<byte[]>($"{entry.Path}: {decoded.Error}", decoded.Exception);
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static Result<EbArchive> Parse(Stream stream, string sourceName, bool leaveOpen)
    {
        long length = stream.Length;
        var header = new byte[HeaderLength];
        stream.Seek(0, SeekOrigin.Begin);
        if (length < HeaderLength)
        {
            return Result.Failure<EbArchive>($"{sourceName} is too short to be an EB archive.");
        }

        stream.ReadExactly(header);
        if (!IsEbArchive(header))
        {
            return Result.Failure<EbArchive>($"{sourceName} is not an EB archive (magic mismatch).");
        }

        uint variant = ReadU32(header, 0x08);
        int rowLength;
        switch (variant)
        {
            case 0x0000_0400: // PS3 and Xbox 360 NHL Legacy
            case 0x0010_0400:
            case 0x0020_0400:
                rowLength = 16;
                break;
            case 0x0011_0400:
                rowLength = 20;
                break;
            default:
                return Result.Failure<EbArchive>($"{sourceName} uses unknown EB variant 0x{variant:X8}.");
        }

        long fileCount = ReadU32(header, 0x04);
        long nameTableOffset = ReadU32(header, 0x0C);
        int nameRecord = header[0x14];
        int folderRecord = header[0x15];
        int folderCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0x16, 2));
        ulong declaredSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x18, 8));

        if (fileCount > 0 && nameRecord < 3)
        {
            return Result.Failure<EbArchive>($"{sourceName} declares a {nameRecord} byte name record, too short to hold a name.");
        }

        long fileTableEnd = FileTableOffset + fileCount * rowLength;
        if (nameTableOffset < fileTableEnd)
        {
            return Result.Failure<EbArchive>($"{sourceName} has overlapping file and name tables.");
        }

        long folderStart = AlignUp(nameTableOffset + fileCount * nameRecord);
        long tableEnd = folderStart + (long)folderCount * folderRecord;
        if (tableEnd > length || tableEnd > MaxTableBytes)
        {
            return Result.Failure<EbArchive>($"{sourceName}'s tables end at {tableEnd:N0}, past the {Math.Min(length, MaxTableBytes):N0} byte limit.");
        }

        var tables = new byte[tableEnd];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(tables);

        var folders = new string[folderCount];
        for (int i = 0; i < folderCount; i++)
        {
            folders[i] = ReadFixedString(tables, checked((int)folderStart + i * folderRecord), folderRecord);
        }

        var warnings = new List<ParseWarning>();
        if (declaredSize != (ulong)length)
        {
            warnings.Add(new ParseWarning($"Header declares {declaredSize:N0} bytes but the archive is {length:N0}.", sourceName));
        }

        var entries = new EbEntry[fileCount];
        int pastEnd = 0;
        for (int i = 0; i < fileCount; i++)
        {
            int row = FileTableOffset + i * rowLength;
            long offset = ReadU32(tables, row) * (long)BlockSize;
            uint storedSize = ReadU32(tables, row + 4);
            uint size = ReadU32(tables, row + 8);
            uint hash = ReadU32(tables, row + 12);
            long span = storedSize != 0 ? storedSize : size;
            if (offset + span > length)
            {
                pastEnd++;
            }

            int nameAt = checked((int)nameTableOffset + i * nameRecord);
            int folderIndex = BinaryPrimitives.ReadUInt16BigEndian(tables.AsSpan(nameAt, 2));
            if (folderIndex >= folderCount && folderCount > 0)
            {
                return Result.Failure<EbArchive>($"{sourceName} file {i} points at folder {folderIndex}, but only {folderCount} exist.");
            }

            string name = ReadFixedString(tables, nameAt + 2, nameRecord - 2);
            string folder = folderCount > 0 ? folders[folderIndex] : string.Empty;
            entries[i] = new EbEntry(i, JoinPath(folder, name), offset, span, hash);
        }

        if (pastEnd > 0)
        {
            warnings.Add(new ParseWarning($"{pastEnd:N0} entries run past the end of the archive and can't be read.", sourceName));
        }

        return Result.Success(new EbArchive(sourceName, stream, leaveOpen, variant, (long)Math.Min(declaredSize, long.MaxValue), entries), warnings);
    }

    private static string JoinPath(string folder, string name)
    {
        string path = folder.Length == 0 || folder.EndsWith('/') || folder.EndsWith('\\') ? folder + name : folder + "/" + name;
        return path.Replace('\\', '/');
    }

    private static string ReadFixedString(byte[] data, int offset, int length)
    {
        ReadOnlySpan<byte> span = data.AsSpan(offset, length);
        int end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? span[..end] : span);
    }

    private static long AlignUp(long value) => (value + BlockSize - 1) / BlockSize * BlockSize;

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
