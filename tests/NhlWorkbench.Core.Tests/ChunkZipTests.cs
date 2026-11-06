using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class ChunkZipTests
{
    private static byte[] Sample(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) ^ (i >> 5));
        }

        return data;
    }

    [Theory]
    [InlineData(1, 1024)]
    [InlineData(1000, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(5000, 1024)]
    public void NewerHeader_RoundTrips(int length, int chunkSize)
    {
        byte[] original = Sample(length);
        Result(original, ChunkZip.Decompress(EbTestData.ChunkZipNewer(original, chunkSize)));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void NewerHeader_RespectsAlignmentField(int alignment)
    {
        byte[] original = Sample(5000);
        Result(original, ChunkZip.Decompress(EbTestData.ChunkZipNewer(original, 1024, alignment)));
    }

    [Fact]
    public void NewerHeader_StoredChunksAreCopied()
    {
        byte[] original = Sample(3000);
        Result(original, ChunkZip.Decompress(EbTestData.ChunkZipNewer(original, 1024, stored: true)));
    }

    [Fact]
    public void NewerHeader_ZlibWrappedChunksAreInflated()
    {
        byte[] original = Sample(3000);
        Result(original, ChunkZip.Decompress(EbTestData.ChunkZipNewer(original, 1024, zlib: true)));
    }

    [Fact]
    public void OlderHeader_RoundTrips()
    {
        byte[] original = Sample(5000);
        Result(original, ChunkZip.Decompress(EbTestData.ChunkZipOlder(original, 1024)));
    }

    [Fact]
    public void RejectsPayloadWithoutTheTag()
    {
        Assert.False(ChunkZip.Decompress(new byte[64]).IsSuccess);
    }

    [Fact]
    public void RejectsTruncatedPayload()
    {
        byte[] whole = EbTestData.ChunkZipNewer(Sample(5000), 1024);
        Assert.False(ChunkZip.Decompress(whole[..(whole.Length / 2)]).IsSuccess);
    }

    [Fact]
    public void RejectsChunksThatStopShortOfTheDeclaredSize()
    {
        byte[] payload = EbTestData.ChunkZipNewer(Sample(1000), 1024);
        EbTestData.PutU32(payload, 12, 1001);
        Assert.False(ChunkZip.Decompress(payload).IsSuccess);
    }

    [Fact]
    public void RejectsChunksThatInflatePastTheDeclaredSize()
    {
        byte[] payload = EbTestData.ChunkZipNewer(Sample(1000), 1024);
        EbTestData.PutU32(payload, 12, 999);
        Assert.False(ChunkZip.Decompress(payload).IsSuccess);
    }

    [Fact]
    public void RejectsImpossibleDeclaredSizeWithoutAllocating()
    {
        byte[] payload = EbTestData.ChunkZipNewer(Sample(100), 1024);
        EbTestData.PutU32(payload, 12, 900_000_000);
        Assert.False(ChunkZip.Decompress(payload).IsSuccess);
    }

    [Fact]
    public void RejectsGarbageChunkData()
    {
        byte[] payload = EbTestData.ChunkZipNewer(Sample(1000), 1024);
        Array.Fill(payload, (byte)0xFF, 0x30, payload.Length - 0x30);
        Assert.False(ChunkZip.Decompress(payload).IsSuccess);
    }

    private static void Result(byte[] expected, Diagnostics.Result<byte[]> actual)
    {
        Assert.True(actual.IsSuccess, actual.Error);
        Assert.Equal(expected, actual.Value);
    }
}
