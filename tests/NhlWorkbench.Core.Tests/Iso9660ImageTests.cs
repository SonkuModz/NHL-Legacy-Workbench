using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Eb;
using NhlWorkbench.Core.Formats.Iso;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public sealed class Iso9660ImageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nhlwb-iso-{Guid.NewGuid():N}.iso");

    public void Dispose() => File.Delete(_path);

    private static byte[] Fill(int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + (i * 3));
        }

        return data;
    }

    private Iso9660Image OpenSample(byte[] one, byte[] first, byte[] second)
    {
        File.WriteAllBytes(_path, EbTestData.IsoImage(one, first, second));
        Result<Iso9660Image> result = Iso9660Image.Open(_path);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    [Fact]
    public void ListsNestedFilesWithoutVersionSuffix()
    {
        Iso9660Image image = OpenSample(Fill(100, 1), Fill(2048, 2), Fill(50, 3));

        Assert.Equal(["SUB/ONE.BIN", "SUB/TWO.BIN"], image.Files.Select(f => f.Path));
        Assert.NotNull(image.Find(@"sub\one.bin"));
    }

    [Fact]
    public void MultiExtentFileReadsAsOneContinuousStream()
    {
        byte[] first = Fill(4096, 2);
        byte[] second = Fill(1000, 3);
        Iso9660Image image = OpenSample(Fill(100, 1), first, second);

        IsoFile two = image.Find("SUB/TWO.BIN")!;
        Assert.Equal(2, two.Extents.Count);
        Assert.Equal(5096, two.Length);

        using Stream stream = image.OpenFile(two);
        var all = new byte[5096];
        stream.ReadExactly(all);
        Assert.Equal(first.Concat(second), all);
        Assert.Equal(0, stream.Read(new byte[10]));
    }

    [Fact]
    public void ReadAcrossTheExtentBoundaryAfterSeeking()
    {
        byte[] first = Fill(4096, 2);
        byte[] second = Fill(1000, 3);
        Iso9660Image image = OpenSample(Fill(100, 1), first, second);

        using Stream stream = image.OpenFile(image.Find("SUB/TWO.BIN")!);
        stream.Seek(4090, SeekOrigin.Begin);
        var span = new byte[12];
        stream.ReadExactly(span);
        Assert.Equal(first[4090..].Concat(second[..6]), span);

        stream.Seek(-1, SeekOrigin.End);
        Assert.Equal(second[^1], stream.ReadByte());
    }

    [Fact]
    public void StreamIsReadOnly()
    {
        Iso9660Image image = OpenSample(Fill(100, 1), Fill(2048, 2), Fill(50, 3));

        using Stream stream = image.OpenFile(image.Files[0]);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [Fact]
    public void EbArchiveOpensFromAMultiExtentFile()
    {
        byte[] payload = Fill(6000, 9);
        byte[] archive = EbTestData.EbArchive([("rendering/a.bin", payload)]);
        int split = archive.Length / 2 / 2048 * 2048;
        Iso9660Image image = OpenSample(Fill(10, 1), archive[..split], archive[split..]);

        using EbArchive eb = EbArchive.Open(image.OpenFile(image.Find("SUB/TWO.BIN")!), "two").Value!;

        Assert.Equal(payload, eb.ReadStored(eb.Find("rendering/a.bin")!).Value);
    }

    [Fact]
    public void RejectsFilesThatAreNotIsoImages()
    {
        File.WriteAllBytes(_path, new byte[64 * 1024]);
        Assert.False(Iso9660Image.Open(_path).IsSuccess);
    }

    [Fact]
    public void RejectsImageTooShortToHoldADescriptor()
    {
        File.WriteAllBytes(_path, new byte[10]);
        Assert.False(Iso9660Image.Open(_path).IsSuccess);
    }

    [Fact]
    public void RealDisc_MainArchivesOpenAndSmallArchiveDecodes()
    {
        if (!SampleFileCatalog.Ps3DiscAvailable)
        {
            return;
        }

        Result<Iso9660Image> opened = Iso9660Image.Open(SampleFileCatalog.Ps3Disc);
        Assert.True(opened.IsSuccess, opened.Error);
        Iso9660Image image = opened.Value!;

        // Over 1 GB, so this one spans two extents.
        IsoFile render = image.Find("PS3_GAME/USRDIR/CACHERENDER.BIG")!;
        Assert.Equal(2, render.Extents.Count);
        using (EbArchive big = EbArchive.Open(image.OpenFile(render), render.Path).Value!)
        {
            Assert.Equal(render.Length, big.DeclaredSize);
            Assert.NotEmpty(big.Entries);
        }

        using EbArchive boot = EbArchive.Open(image.OpenFile(image.Find("PS3_GAME/USRDIR/BOOT.BIG")!), "BOOT.BIG").Value!;
        Assert.Equal(469, boot.Entries.Count);
        foreach (EbEntry entry in boot.Entries)
        {
            Result<byte[]> decoded = boot.ReadDecoded(entry);
            Assert.True(decoded.IsSuccess, decoded.Error);
        }
    }
}
