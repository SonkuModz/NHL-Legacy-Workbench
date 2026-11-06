using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NhlWorkbench.Core.Tests.TestData;

/// <summary>Builds small synthetic chunkzip payloads, EB archives and ISO images.</summary>
public static class EbTestData
{
    public static void PutU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);

    public static byte[] ChunkZipNewer(byte[] original, int chunkSize, int alignment = 16, bool stored = false, bool zlib = false)
    {
        var output = new MemoryStream();
        output.Write("chunkzip"u8);
        int chunkCount = original.Length == 0 ? 0 : (original.Length + chunkSize - 1) / chunkSize;
        WriteU32(output, 2);
        WriteU32(output, (uint)original.Length);
        WriteU32(output, (uint)chunkSize);
        WriteU32(output, (uint)chunkCount);
        WriteU32(output, (uint)alignment);
        output.SetLength(0x28);
        output.Position = 0x28;

        for (int i = 0; i < chunkCount; i++)
        {
            ReadOnlySpan<byte> chunk = original.AsSpan(i * chunkSize, Math.Min(chunkSize, original.Length - (i * chunkSize)));
            byte[] body = stored ? chunk.ToArray() : zlib ? Zlib(chunk) : Deflate(chunk);

            long dataStart = (output.Position + 8 + alignment - 1) / alignment * alignment;
            output.SetLength(dataStart - 8);
            output.Position = dataStart - 8;
            WriteU32(output, (uint)body.Length);
            WriteU32(output, stored ? 4u : 1u);
            output.Write(body);
        }

        return output.ToArray();
    }

    public static byte[] ChunkZipOlder(byte[] original, int chunkSize)
    {
        var output = new MemoryStream();
        output.Write("chunkzip"u8);
        WriteU32(output, (uint)original.Length);
        WriteU32(output, (uint)chunkSize);
        for (int start = 0; start < original.Length; start += chunkSize)
        {
            byte[] body = Deflate(original.AsSpan(start, Math.Min(chunkSize, original.Length - start)));
            WriteU32(output, (uint)body.Length);
            output.Write(body);
        }

        return output.ToArray();
    }

    /// <summary>Builds an EB archive. Paths are split into folder and name at the last slash.</summary>
    public static byte[] EbArchive(IReadOnlyList<(string Path, byte[] Payload)> files, uint variant = 0x400)
    {
        int rowLength = variant == 0x0011_0400 ? 20 : 16;
        var folders = new List<string>();
        var names = new List<(int Folder, string Name)>();
        foreach ((string path, _) in files)
        {
            int slash = path.LastIndexOf('/');
            string folder = slash >= 0 ? path[..slash] : string.Empty;
            if (!folders.Contains(folder))
            {
                folders.Add(folder);
            }

            names.Add((folders.IndexOf(folder), path[(slash + 1)..]));
        }

        int nameRecord = names.Count == 0 ? 3 : names.Max(n => n.Name.Length) + 3;
        int folderRecord = folders.Count == 0 ? 1 : folders.Max(f => f.Length) + 1;
        int nameTable = 0x30 + (files.Count * rowLength);
        int folderTable = AlignUp16(nameTable + (files.Count * nameRecord));
        int dataStart = AlignUp16(folderTable + (folders.Count * folderRecord));

        long end = dataStart;
        var offsets = new long[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            offsets[i] = end;
            end = AlignUp16((int)end + files[i].Payload.Length);
        }

        var archive = new byte[end];
        "EB\0\u0003"u8.CopyTo(archive);
        PutU32(archive, 0x04, (uint)files.Count);
        PutU32(archive, 0x08, variant);
        PutU32(archive, 0x0C, (uint)nameTable);
        archive[0x14] = (byte)nameRecord;
        archive[0x15] = (byte)folderRecord;
        BinaryPrimitives.WriteUInt16BigEndian(archive.AsSpan(0x16, 2), (ushort)folders.Count);
        BinaryPrimitives.WriteUInt64BigEndian(archive.AsSpan(0x18, 8), (ulong)end);

        for (int i = 0; i < files.Count; i++)
        {
            int row = 0x30 + (i * rowLength);
            PutU32(archive, row, (uint)(offsets[i] / 16));
            PutU32(archive, row + 8, (uint)files[i].Payload.Length);
            PutU32(archive, row + 12, (uint)(i + 100));

            int nameAt = nameTable + (i * nameRecord);
            BinaryPrimitives.WriteUInt16BigEndian(archive.AsSpan(nameAt, 2), (ushort)names[i].Folder);
            Encoding.ASCII.GetBytes(names[i].Name).CopyTo(archive, nameAt + 2);
            files[i].Payload.CopyTo(archive, offsets[i]);
        }

        for (int i = 0; i < folders.Count; i++)
        {
            Encoding.ASCII.GetBytes(folders[i]).CopyTo(archive, folderTable + (i * folderRecord));
        }

        return archive;
    }

    /// <summary>
    /// Writes a minimal ISO 9660 image: a root folder holding SUB/, with one single-extent file and
    /// one file split across two non-adjacent extents.
    /// </summary>
    public static byte[] IsoImage(byte[] singleExtentFile, byte[] firstPart, byte[] secondPart)
    {
        const int sector = 2048;
        const int rootLba = 18;
        const int subLba = 19;
        const int singleLba = 20;
        int firstLba = singleLba + Sectors(singleExtentFile.Length);
        int secondLba = firstLba + Sectors(firstPart.Length) + 1; // leaves a gap so the extents aren't adjacent
        var image = new byte[(secondLba + Sectors(secondPart.Length)) * sector];

        image[16 * sector] = 1;
        "CD001"u8.CopyTo(image.AsSpan((16 * sector) + 1));
        WriteDirectoryRecord(image, (16 * sector) + 156, [0], rootLba, sector, 0x02);

        int at = rootLba * sector;
        at += WriteDirectoryRecord(image, at, [0], rootLba, sector, 0x02);
        at += WriteDirectoryRecord(image, at, [1], rootLba, sector, 0x02);
        WriteDirectoryRecord(image, at, "SUB"u8.ToArray(), subLba, sector, 0x02);

        at = subLba * sector;
        at += WriteDirectoryRecord(image, at, [0], subLba, sector, 0x02);
        at += WriteDirectoryRecord(image, at, [1], rootLba, sector, 0x02);
        at += WriteDirectoryRecord(image, at, "ONE.BIN;1"u8.ToArray(), singleLba, singleExtentFile.Length, 0);
        at += WriteDirectoryRecord(image, at, "TWO.BIN;1"u8.ToArray(), firstLba, firstPart.Length, 0x80);
        WriteDirectoryRecord(image, at, "TWO.BIN;1"u8.ToArray(), secondLba, secondPart.Length, 0);

        singleExtentFile.CopyTo(image, singleLba * sector);
        firstPart.CopyTo(image, firstLba * sector);
        secondPart.CopyTo(image, secondLba * sector);
        return image;
    }

    private static int WriteDirectoryRecord(byte[] image, int at, byte[] name, int lba, int length, byte flags)
    {
        int recordLength = (33 + name.Length + 1) & ~1;
        image[at] = (byte)recordLength;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 2, 4), lba);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 10, 4), length);
        image[at + 25] = flags;
        image[at + 32] = (byte)name.Length;
        name.CopyTo(image, at + 33);
        return recordLength;
    }

    private static int Sectors(int length) => (length + 2047) / 2048;

    private static int AlignUp16(int value) => (value + 15) & ~15;

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Zlib(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
