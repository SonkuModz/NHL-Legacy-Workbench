using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Eb;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class EbArchiveTests
{
    private static readonly byte[] Plain = [1, 2, 3, 4, 5];

    private static byte[] Compressible()
    {
        var data = new byte[4000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i / 50);
        }

        return data;
    }

    private static Result<EbArchive> Open(byte[] archive) => EbArchive.Open(new MemoryStream(archive), "test.big");

    [Fact]
    public void ListsPathsAcrossFolders()
    {
        byte[] data = EbTestData.EbArchive([("root.ini", Plain), ("rendering/logo/a.rpsgl", Plain), ("rendering/logo/b.rpsgl", Plain), ("db/nhlng.db", Plain)]);

        Result<EbArchive> result = Open(data);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Warnings);
        using EbArchive archive = result.Value!;
        Assert.Equal(["root.ini", "rendering/logo/a.rpsgl", "rendering/logo/b.rpsgl", "db/nhlng.db"], archive.Entries.Select(e => e.Path));
        Assert.Equal(0x400u, archive.Variant);
        Assert.Equal(data.Length, archive.DeclaredSize);
    }

    [Fact]
    public void ReadsStoredBytesUnchanged()
    {
        byte[] wrapped = EbTestData.ChunkZipNewer(Compressible(), 1024);
        using EbArchive archive = Open(EbTestData.EbArchive([("a.bin", Plain), ("b.bin", wrapped)])).Value!;

        Assert.Equal(Plain, archive.ReadStored(archive.Entries[0]).Value);
        Assert.Equal(wrapped, archive.ReadStored(archive.Entries[1]).Value);
    }

    [Fact]
    public void ReadDecodedRemovesChunkZipAndPassesOtherPayloadsThrough()
    {
        using EbArchive archive = Open(EbTestData.EbArchive([("plain.bin", Plain), ("zip.bin", EbTestData.ChunkZipNewer(Compressible(), 1024))])).Value!;

        Assert.Equal(Plain, archive.ReadDecoded(archive.Entries[0]).Value);
        Assert.Equal(Compressible(), archive.ReadDecoded(archive.Entries[1]).Value);
    }

    [Fact]
    public void FindIgnoresCaseAndAcceptsBackslashes()
    {
        using EbArchive archive = Open(EbTestData.EbArchive([("rendering/logo/a.rpsgl", Plain)])).Value!;

        Assert.NotNull(archive.Find(@"RENDERING\LOGO\A.RPSGL"));
        Assert.Null(archive.Find("rendering/logo/missing.rpsgl"));
    }

    [Fact]
    public void WideRowVariantIsRead()
    {
        using EbArchive archive = Open(EbTestData.EbArchive([("a.bin", Plain), ("b/c.bin", Plain)], variant: 0x0011_0400)).Value!;

        Assert.Equal(0x0011_0400u, archive.Variant);
        Assert.Equal(Plain, archive.ReadStored(archive.Find("b/c.bin")!).Value);
    }

    [Fact]
    public void EmptyArchiveOpens()
    {
        Result<EbArchive> result = Open(EbTestData.EbArchive([]));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value!.Entries);
    }

    [Fact]
    public void DeclaredSizeMismatchIsAWarningNotAFailure()
    {
        byte[] data = EbTestData.EbArchive([("a.bin", Plain)]);
        data[0x1F] ^= 0x10;

        Result<EbArchive> result = Open(data);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void EntryPastEndWarnsAtOpenAndFailsOnRead()
    {
        byte[] data = EbTestData.EbArchive([("a.bin", Plain)]);
        EbTestData.PutU32(data, 0x30 + 8, 1_000_000);

        Result<EbArchive> result = Open(data);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Single(result.Warnings);
        Assert.False(result.Value!.ReadStored(result.Value.Entries[0]).IsSuccess);
    }

    [Fact]
    public void RejectsWrongMagic()
    {
        byte[] data = EbTestData.EbArchive([("a.bin", Plain)]);
        data[0] = (byte)'X';
        Assert.False(Open(data).IsSuccess);
    }

    [Fact]
    public void RejectsUnknownVariant()
    {
        byte[] data = EbTestData.EbArchive([("a.bin", Plain)]);
        EbTestData.PutU32(data, 0x08, 0x1234);
        Assert.False(Open(data).IsSuccess);
    }

    [Fact]
    public void RejectsTablesPastEndOfFile()
    {
        byte[] data = EbTestData.EbArchive([("a.bin", Plain)]);
        EbTestData.PutU32(data, 0x04, uint.MaxValue);
        EbTestData.PutU32(data, 0x0C, uint.MaxValue);
        Assert.False(Open(data).IsSuccess);
    }

    [Fact]
    public void RejectsFolderIndexOutOfRange()
    {
        byte[] data = EbTestData.EbArchive([("f/a.bin", Plain)]);
        int nameTable = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0x0C, 4));
        data[nameTable] = 0xFF;
        Assert.False(Open(data).IsSuccess);
    }

    [Fact]
    public void RejectsTruncatedHeaderAndMissingFile()
    {
        Assert.False(Open(new byte[8]).IsSuccess);
        Assert.False(EbArchive.OpenFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".big")).IsSuccess);
    }

    [Fact]
    public void OwnsStreamUnlessToldToLeaveItOpen()
    {
        var kept = new MemoryStream(EbTestData.EbArchive([("a.bin", Plain)]));
        EbArchive.Open(kept, "kept", leaveOpen: true).Value!.Dispose();
        Assert.True(kept.CanRead);

        var owned = new MemoryStream(EbTestData.EbArchive([("a.bin", Plain)]));
        EbArchive.Open(owned, "owned").Value!.Dispose();
        Assert.False(owned.CanRead);
    }
}
