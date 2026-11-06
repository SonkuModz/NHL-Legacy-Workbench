using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.JerseySlots;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class SlotTextureFileTests
{
    private static string Compiled => Path.GetFullPath(Path.Combine(SampleFileCatalog.JerseyDestinationRoot, "..", ".."));

    private static string? RealPath(long style, long art, long variant, int assetIndex)
    {
        JerseySlotEditor.SlotAsset asset = JerseySlotEditor.AssetsFor(style, art, variant)[assetIndex];
        string path = Path.Combine(Compiled, asset.RelativeFolder, asset.FileName);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void ImageResampler_ResizesAndKeepsColours()
    {
        var red = new Rgba32Image { Width = 2, Height = 2, Pixels = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 200, 10, 10, 255 }).ToArray() };
        Rgba32Image big = ImageResampler.Resize(red, 8, 8);
        Assert.Equal(8, big.Width);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(200, big.Pixels[i * 4]));
        Assert.Same(red, ImageResampler.Resize(red, 2, 2));
    }

    [Fact]
    public void RealSlotFiles_ListTheirTextures()
    {
        if (RealPath(1, 15, 0, 0) is null)
        {
            return;
        }

        int[] expected = { 6, 1, 1, 3, 1 }; // jersey set, name plate, menu art, pants, socks
        for (int i = 0; i < expected.Length; i++)
        {
            string? path = RealPath(1, 15, 0, i);
            Assert.NotNull(path);
            Result<SlotTextureFile> file = SlotTextureFile.Load(path!);
            Assert.True(file.IsSuccess, file.Error);
            Assert.Equal(expected[i], file.Value!.Textures.Count);
            Assert.Equal(i == 2 ? SlotTextureFileKind.MenuArt : SlotTextureFileKind.Rx2, file.Value.Kind);
        }

        TextureEntry preview = SlotTextureFile.Load(RealPath(1, 15, 0, 2)!).Value!.Textures[0];
        Assert.Equal((256, 256, TextureFormat.Argb8888), (preview.Width, preview.Height, preview.Format));
    }

    [Fact]
    public void MenuArt_ReplacedFromAnyShapedPicture_IsResizedWrittenAndTheRestOfThePackageIsUntouched()
    {
        string? path = RealPath(1, 15, 0, 2);
        if (path is null)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        SlotTextureFile file = SlotTextureFile.Load(path).Value!;
        TextureEntry old = file.Textures[0];

        // A 100x50 left-red / right-blue picture, supplied as a DDS (the PNG path goes through the same re-encode).
        var codec = new DdsCodec();
        var pixels = new byte[100 * 50 * 4];
        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                int o = ((y * 100) + x) * 4;
                bool left = x < 50;
                pixels[o] = (byte)(left ? 255 : 0);
                pixels[o + 2] = (byte)(left ? 0 : 255);
                pixels[o + 3] = 255;
            }
        }

        Result<DecodedDdsTexture> encodedResult = codec.EncodeToFormat(new Rgba32Image { Width = 100, Height = 50, Pixels = pixels }, TextureFormat.Argb8888, 1);
        Assert.True(encodedResult.IsSuccess, encodedResult.Error);
        DecodedDdsTexture encoded = encodedResult.Value!;
        byte[] dds = codec.WrapAsDds(encoded.LinearData, 100, 50, TextureFormat.Argb8888, 1).Value!;

        Result<SlotTextureReplacer.Result> replacement = SlotTextureReplacer.Build(codec, old, dds, isDds: true, allowShapeChange: false);
        Assert.True(replacement.IsSuccess, replacement.Error);
        Assert.False(replacement.Value!.ShapeChanged);
        Assert.Equal((256, 256, TextureFormat.Argb8888), (replacement.Value.Entry.Width, replacement.Value.Entry.Height, replacement.Value.Entry.Format));

        Result<byte[]> built = file.Build(new Dictionary<int, TextureEntry> { [0] = replacement.Value.Entry });
        Assert.True(built.IsSuccess, built.Error);
        Assert.True(BigfArchive.IsBigf(built.Value!));

        // The picture reads back as the red/blue split (BGRA memory order).
        TextureEntry back = SlotTextureFile.Load(path, built.Value!).Value!.Textures[0];
        int leftPixel = ((128 * 256) + 20) * 4;
        int rightPixel = ((128 * 256) + 235) * 4;
        Assert.True(back.LinearPixelData[leftPixel + 2] > 200 && back.LinearPixelData[leftPixel] < 50, "left side should be red");
        Assert.True(back.LinearPixelData[rightPixel] > 200 && back.LinearPixelData[rightPixel + 2] < 50, "right side should be blue");

        // Every other entry of the package is byte-identical to the original's.
        BigfArchive before = BigfArchive.Parse(original, path).Value!;
        BigfArchive after = BigfArchive.Parse(built.Value!, path).Value!;
        Assert.Equal(before.Entries.Select(e => e.Name), after.Entries.Select(e => e.Name));
        foreach (BigfEntry entry in before.Entries.Where(e => e.Name != "2"))
        {
            Assert.Equal(before.GetEntryData(entry.Name), after.GetEntryData(entry.Name));
        }
    }

    [Fact]
    public void MenuArt_RewritingTheUnchangedPictureKeepsItsPixelsExactly()
    {
        string? path = RealPath(1, 15, 0, 2);
        if (path is null)
        {
            return;
        }

        SlotTextureFile file = SlotTextureFile.Load(path).Value!;
        Result<byte[]> built = file.Build(new Dictionary<int, TextureEntry> { [0] = file.Textures[0] });
        Assert.True(built.IsSuccess, built.Error);
        Assert.Equal(file.Textures[0].LinearPixelData, SlotTextureFile.Load(path, built.Value!).Value!.Textures[0].LinearPixelData);
    }

    [Fact]
    public void Rx2_ReplaceWithSameShapeAndWithADifferentSize_BothReadBack()
    {
        string? path = RealPath(1, 15, 0, 3); // pants: small and quick
        if (path is null)
        {
            return;
        }

        var codec = new DdsCodec();
        SlotTextureFile file = SlotTextureFile.Load(path).Value!;
        TextureEntry cm = file.Textures[0];

        // Same shape: the file's own pixels exported as a DDS and put back.
        byte[] dds = codec.WrapAsDds(cm.LinearPixelData, cm.Width, cm.Height, cm.Format, cm.MipCount).Value!;
        SlotTextureReplacer.Result same = SlotTextureReplacer.Build(codec, cm, dds, isDds: true, allowShapeChange: true).Value!;
        Assert.False(same.ShapeChanged);
        Result<byte[]> builtSame = file.Build(new Dictionary<int, TextureEntry> { [0] = same.Entry });
        Assert.True(builtSame.IsSuccess, builtSame.Error);
        Assert.Equal(cm.LinearPixelData, SlotTextureFile.Load(path, builtSame.Value!).Value!.Textures[0].LinearPixelData);

        // Different size (half): a rebuild; the other textures survive.
        Rgba32Image decoded = codec.Decode(cm.LinearPixelData, cm.Width, cm.Height, cm.Format).Value!;
        Rgba32Image half = ImageResampler.Resize(decoded, cm.Width / 2, cm.Height / 2);
        Result<DecodedDdsTexture> smallerResult = codec.EncodeToFormat(half, cm.Format, Math.Max(1, cm.MipCount - 1));
        Assert.True(smallerResult.IsSuccess, smallerResult.Error);
        DecodedDdsTexture smaller = smallerResult.Value!;
        byte[] smallDds = codec.WrapAsDds(smaller.LinearData, smaller.Width, smaller.Height, smaller.Format, smaller.MipCount).Value!;
        SlotTextureReplacer.Result changed = SlotTextureReplacer.Build(codec, cm, smallDds, isDds: true, allowShapeChange: true).Value!;
        Assert.True(changed.ShapeChanged);

        Result<byte[]> builtChanged = file.Build(new Dictionary<int, TextureEntry> { [0] = changed.Entry });
        Assert.True(builtChanged.IsSuccess, builtChanged.Error);
        SlotTextureFile reread = SlotTextureFile.Load(path, builtChanged.Value!).Value!;
        Assert.Equal(cm.Width / 2, reread.Textures[0].Width);
        Assert.Equal(file.Textures[1].LinearPixelData, reread.Textures[1].LinearPixelData);
        Assert.Equal(file.Textures[2].LinearPixelData, reread.Textures[2].LinearPixelData);
    }
}

/// <summary>Pictures keep their exact pixel values when imported (they used to be darkened by an sRGB curve).</summary>
public class PictureImportColourTests
{
    private static byte[] MinimalPng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            byte[] typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(typeAndData);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in typeAndData)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            var crcBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
            output.Write(crcBytes);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk("IHDR", header);

        var raw = new byte[height * ((width * 4) + 1)];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(rgba, y * width * 4, raw, (y * ((width * 4) + 1)) + 1, width * 4);
        }

        using var deflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        Chunk("IDAT", deflated.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    [Fact]
    public void ImportedPng_KeepsItsExactColours()
    {
        byte[] pixels = Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 30, 220, 10, 255 }).ToArray();
        Result<Rgba32Image> decoded = new DdsCodec().DecodeImageFile(MinimalPng(4, 4, pixels));
        Assert.True(decoded.IsSuccess, decoded.Error);
        Assert.Equal(new byte[] { 30, 220, 10, 255 }, decoded.Value!.Pixels.Take(4).ToArray());
    }
}
