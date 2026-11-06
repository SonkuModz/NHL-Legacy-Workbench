using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Iso;

/// <summary>A contiguous run of a file's bytes on the disc image.</summary>
public readonly record struct IsoExtent(long Offset, long Length);

/// <summary>A file on the disc. Files of 1 GB or more span several extents.</summary>
public sealed record IsoFile(string Path, long Length, IReadOnlyList<IsoExtent> Extents);

/// <summary>
/// Read-only ISO 9660 reader for a decrypted PS3 disc image. The image is never written.
/// Each <see cref="OpenFile"/> stream has its own file handle, so streams can be used concurrently.
/// </summary>
public sealed class Iso9660Image
{
    private const int SectorSize = 2048;
    private const int PrimaryDescriptorSector = 16;
    private const int RootRecordOffset = 156;
    private const int MaxDepth = 32;
    private const int MaxFiles = 1_000_000;
    private const byte FlagDirectory = 0x02;
    private const byte FlagMultiExtent = 0x80;

    private readonly string _imagePath;
    private readonly Dictionary<string, IsoFile> _byPath;

    private Iso9660Image(string imagePath, IReadOnlyList<IsoFile> files)
    {
        _imagePath = imagePath;
        Files = files;
        _byPath = new Dictionary<string, IsoFile>(files.Count, StringComparer.OrdinalIgnoreCase);
        foreach (IsoFile file in files)
        {
            _byPath.TryAdd(file.Path, file);
        }
    }

    public IReadOnlyList<IsoFile> Files { get; }

    public static Result<Iso9660Image> Open(string imagePath)
    {
        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var descriptor = new byte[SectorSize];
            stream.Seek(PrimaryDescriptorSector * (long)SectorSize, SeekOrigin.Begin);
            stream.ReadExactly(descriptor);
            if (descriptor[0] != 1 || !descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                return Result.Failure<Iso9660Image>($"{imagePath} is not an ISO 9660 image.");
            }

            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(RootRecordOffset + 2, 4));
            uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(RootRecordOffset + 10, 4));

            var files = new List<IsoFile>();
            var visited = new HashSet<uint>();
            string? error = WalkDirectory(stream, rootLba, rootSize, string.Empty, 0, files, visited);
            return error is null
                ? Result.Success(new Iso9660Image(imagePath, files))
                : Result.Failure<Iso9660Image>($"{imagePath}: {error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Failure<Iso9660Image>($"Can't read {imagePath}: {ex.Message}", ex);
        }
    }

    /// <summary>Finds a file by its path, ignoring case. Use forward slashes.</summary>
    public IsoFile? Find(string path) => _byPath.GetValueOrDefault(path.Replace('\\', '/'));

    /// <summary>Opens a read-only, seekable view of the file. The caller owns the stream.</summary>
    public Stream OpenFile(IsoFile file) => new ExtentStream(_imagePath, file);

    private static string? WalkDirectory(FileStream stream, uint lba, uint size, string prefix, int depth, List<IsoFile> files, HashSet<uint> visited)
    {
        if (depth > MaxDepth)
        {
            return "directories are nested too deeply.";
        }

        if (!visited.Add(lba))
        {
            return "a directory points back at itself.";
        }

        long start = lba * (long)SectorSize;
        if (size > 64 * 1024 * 1024 || start + size > stream.Length)
        {
            return $"directory {prefix} lies outside the image.";
        }

        var data = new byte[size];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(data);

        var pendingExtents = new List<IsoExtent>();
        int position = 0;
        while (position < data.Length)
        {
            int recordLength = data[position];
            if (recordLength == 0)
            {
                // The rest of this sector is padding.
                position = (position / SectorSize + 1) * SectorSize;
                continue;
            }

            if (recordLength < 34 || position + recordLength > data.Length)
            {
                return $"directory {prefix} has a malformed record.";
            }

            ReadOnlySpan<byte> record = data.AsSpan(position, recordLength);
            position += recordLength;

            int nameLength = record[32];
            if (33 + nameLength > recordLength)
            {
                return $"directory {prefix} has a malformed name.";
            }

            ReadOnlySpan<byte> rawName = record.Slice(33, nameLength);
            if (nameLength == 1 && rawName[0] <= 1)
            {
                continue; // "." and ".."
            }

            string name = Encoding.Latin1.GetString(rawName);
            int version = name.IndexOf(';');
            if (version >= 0)
            {
                name = name[..version];
            }

            long extentLba = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4)) + (long)record[1];
            uint extentLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(10, 4));
            byte flags = record[25];
            string path = prefix + name;

            if ((flags & FlagDirectory) != 0)
            {
                string? error = WalkDirectory(stream, (uint)extentLba, extentLength, path + "/", depth + 1, files, visited);
                if (error is not null)
                {
                    return error;
                }

                continue;
            }

            long extentOffset = extentLba * SectorSize;
            if (extentOffset + extentLength > stream.Length)
            {
                return $"{path} lies outside the image.";
            }

            pendingExtents.Add(new IsoExtent(extentOffset, extentLength));
            if ((flags & FlagMultiExtent) != 0)
            {
                continue;
            }

            if (files.Count >= MaxFiles)
            {
                return "the image lists too many files.";
            }

            files.Add(new IsoFile(path, pendingExtents.Sum(e => e.Length), pendingExtents.ToArray()));
            pendingExtents.Clear();
        }

        return pendingExtents.Count == 0 ? null : $"directory {prefix} ends in the middle of a multi-extent file.";
    }

    /// <summary>Presents a file's extents as one continuous read-only stream.</summary>
    private sealed class ExtentStream : Stream
    {
        private readonly FileStream _image;
        private readonly IsoFile _file;
        private long _position;

        public ExtentStream(string imagePath, IsoFile file)
        {
            _image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _file = file;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _file.Length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            long extentStart = 0;
            foreach (IsoExtent extent in _file.Extents)
            {
                long extentEnd = extentStart + extent.Length;
                if (buffer.Length > 0 && _position < extentEnd)
                {
                    long within = _position - extentStart;
                    int wanted = (int)Math.Min(buffer.Length, extent.Length - within);
                    _image.Seek(extent.Offset + within, SeekOrigin.Begin);
                    int read = _image.Read(buffer[..wanted]);
                    _position += read;
                    total += read;
                    buffer = buffer[read..];
                    if (read < wanted)
                    {
                        break;
                    }
                }

                extentStart = extentEnd;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
