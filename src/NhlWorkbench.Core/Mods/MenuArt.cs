using System.Buffers.Binary;
using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.GameFiles;

namespace NhlWorkbench.Core.Mods;

/// <summary>
/// Menu art: the crests and flat jerseys under fe/ion/artassets. Each is a small BIGF package holding its picture
/// as a compressed .dds, next to data that says where the menus draw it. Only the picture is ever changed, and it
/// keeps its size and format, so that data stays true.
/// </summary>
public static class MenuArt
{
    private const int DdsHeaderSize = 128;

    public static bool IsPackage(string gamePath) => gamePath.EndsWith(".big", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists the pictures in a package. A package with none, or that can't be read, lists nothing.</summary>
    public static IReadOnlyList<TextureInfo> List(byte[] package, string sourceName)
    {
        Result<NestedArchive> opened = NestedArchive.Open(package, sourceName);
        if (!opened.IsSuccess)
        {
            return [];
        }

        var found = new List<TextureInfo>();
        foreach (NestedEntry entry in opened.Value!.Entries.Where(e => e.StoredSize > 0))
        {
            Result<byte[]> member = opened.Value.Read(entry.Name);
            if (member.IsSuccess && TryReadHeader(member.Value!, out int width, out int height, out int mips, out TextureFormat format))
            {
                found.Add(new TextureInfo(found.Count, entry.Name, width, height, format, mips));
            }
        }

        return found;
    }

    /// <summary>Reads one picture's pixels, with every mip.</summary>
    public static Result<DecodedDdsTexture> Read(byte[] package, string sourceName, int textureIndex, DdsCodec codec)
    {
        Result<byte[]> dds = FindMember(package, sourceName, textureIndex, out _);
        return dds.IsSuccess ? codec.ReadDdsFile(dds.Value!) : Result.Failure<DecodedDdsTexture>(dds.Error!);
    }

    /// <summary>The picture as the game stores it, which already is a complete .dds file.</summary>
    public static Result<ExportedDds> Export(byte[] package, string sourceName, int textureIndex)
    {
        Result<byte[]> dds = FindMember(package, sourceName, textureIndex, out string name);
        if (!dds.IsSuccess)
        {
            return Result.Failure<ExportedDds>(dds.Error!);
        }

        return TryReadHeader(dds.Value!, out int width, out int height, out int mips, out TextureFormat format)
            ? Result.Success(new ExportedDds(name, textureIndex, format, width, height, mips, dds.Value!))
            : Result.Failure<ExportedDds>($"{sourceName} holds a picture in a format this app can't read.");
    }

    /// <summary>Fits a picture inside the original's size, converts it to the original's format and returns the whole new package.</summary>
    public static Result<byte[]> Replace(byte[] package, string sourceName, Rgba32Image picture, DdsCodec codec, int textureIndex = 0)
    {
        Result<byte[]> current = FindMember(package, sourceName, textureIndex, out string name);
        if (!current.IsSuccess)
        {
            return current;
        }

        if (!TryReadHeader(current.Value!, out int width, out int height, out int mips, out TextureFormat format))
        {
            return Result.Failure<byte[]>($"{sourceName} holds a picture in a format this app can't read.");
        }

        Result<Rgba32Image> fitted = codec.FitInside(picture, width, height);
        if (!fitted.IsSuccess)
        {
            return Result.Failure<byte[]>(fitted.Error!, fitted.Exception);
        }

        Result<DecodedDdsTexture> encoded = codec.EncodeToFormat(fitted.Value!, format, mips);
        if (!encoded.IsSuccess)
        {
            return Result.Failure<byte[]>(encoded.Error!, encoded.Exception);
        }

        Result<byte[]> wrapped = codec.WrapAsDds(encoded.Value!.LinearData, width, height, format, mips);
        return wrapped.IsSuccess ? Store(package, sourceName, name, wrapped.Value!) : Result.Failure<byte[]>(wrapped.Error!, wrapped.Exception);
    }

    /// <summary>Puts a .dds in. The same size and format goes in as it is, with its mips adjusted if needed. Anything else is converted to fit, with a warning.</summary>
    public static Result<byte[]> Import(byte[] package, string sourceName, byte[] ddsBytes, DdsCodec codec, int textureIndex = 0)
    {
        Result<DecodedDdsTexture> read = codec.ReadDdsFile(ddsBytes);
        if (!read.IsSuccess)
        {
            return Result.Failure<byte[]>("That DDS can't be read. Save it as DXT1, DXT3 or DXT5 and try again.", read.Exception);
        }

        Result<byte[]> current = FindMember(package, sourceName, textureIndex, out string name);
        if (!current.IsSuccess)
        {
            return current;
        }

        if (!TryReadHeader(current.Value!, out int width, out int height, out int mips, out TextureFormat format))
        {
            return Result.Failure<byte[]>($"{sourceName} holds a picture in a format this app can't read.");
        }

        DecodedDdsTexture dds = read.Value!;
        if (dds.Format != format || dds.Width != width || dds.Height != height)
        {
            Result<Rgba32Image> picture = codec.Decode(dds.LinearData, dds.Width, dds.Height, dds.Format);
            if (!picture.IsSuccess)
            {
                return Result.Failure<byte[]>("That DDS can't be read. Save it as DXT1, DXT3 or DXT5 and try again.", picture.Exception);
            }

            Result<byte[]> replaced = Replace(package, sourceName, picture.Value!, codec, textureIndex);
            return replaced.IsSuccess
                ? Result.Success(replaced.Value!, [new ParseWarning($"Your DDS is {dds.Width} × {dds.Height} {dds.Format}, and this picture is {width} × {height} {format}. It was converted to fit, which recompresses it.", sourceName)])
                : replaced;
        }

        Result<DecodedDdsTexture> matched = codec.AdjustMipCount(dds, mips);
        if (!matched.IsSuccess)
        {
            return Result.Failure<byte[]>(matched.Error!, matched.Exception);
        }

        Result<byte[]> wrapped = codec.WrapAsDds(matched.Value!.LinearData, width, height, format, mips);
        if (!wrapped.IsSuccess)
        {
            return Result.Failure<byte[]>(wrapped.Error!, wrapped.Exception);
        }

        Result<byte[]> stored = Store(package, sourceName, name, wrapped.Value!);
        return stored.IsSuccess && dds.MipCount != mips
            ? Result.Success(stored.Value!, [new ParseWarning($"Your DDS had {dds.MipCount} mips and this picture needs {mips}, so the mips were adjusted to match.", sourceName)])
            : stored;
    }

    private static Result<byte[]> FindMember(byte[] package, string sourceName, int textureIndex, out string name)
    {
        name = string.Empty;
        Result<NestedArchive> opened = NestedArchive.Open(package, sourceName);
        if (!opened.IsSuccess)
        {
            return Result.Failure<byte[]>(opened.Error!, opened.Exception);
        }

        int seen = 0;
        foreach (NestedEntry entry in opened.Value!.Entries.Where(e => e.StoredSize > 0))
        {
            Result<byte[]> member = opened.Value.Read(entry.Name);
            if (member.IsSuccess && TextureDds.IsDds(member.Value!) && seen++ == textureIndex)
            {
                name = entry.Name;
                return member;
            }
        }

        return Result.Failure<byte[]>($"{sourceName} holds no picture number {textureIndex + 1}.");
    }

    /// <summary>Puts the new .dds back, packed the way the old one was. Every other member is carried over byte for byte.</summary>
    private static Result<byte[]> Store(byte[] package, string sourceName, string memberName, byte[] dds)
    {
        Result<BigfArchive> archive = BigfArchive.Parse(package, sourceName);
        if (!archive.IsSuccess)
        {
            return Result.Failure<byte[]>(archive.Error!, archive.Exception);
        }

        if (!BigfArchive.IsBigf(package))
        {
            return Result.Failure<byte[]>($"{sourceName} is a kind of package this app can't rebuild yet.");
        }

        byte[] stored = dds;
        if (RefPack.IsRefPack(archive.Value!.GetEntryData(memberName)))
        {
            Result<byte[]> packed = RefPack.Compress(dds);
            if (!packed.IsSuccess)
            {
                return Result.Failure<byte[]>(packed.Error!, packed.Exception);
            }

            stored = packed.Value!;
        }

        return Result.Success(archive.Value.RebuildReplacingEntry(memberName, stored));
    }

    /// <summary>Reads just enough of a .dds header to describe it, far quicker than loading the file.</summary>
    private static bool TryReadHeader(ReadOnlySpan<byte> dds, out int width, out int height, out int mips, out TextureFormat format)
    {
        width = height = mips = 0;
        format = default;
        if (dds.Length < DdsHeaderSize || !TextureDds.IsDds(dds))
        {
            return false;
        }

        height = BinaryPrimitives.ReadInt32LittleEndian(dds[12..]);
        width = BinaryPrimitives.ReadInt32LittleEndian(dds[16..]);
        mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(dds[28..]));

        ReadOnlySpan<byte> fourCc = dds.Slice(84, 4);
        if (fourCc.SequenceEqual("DXT1"u8))
        {
            format = TextureFormat.Dxt1;
        }
        else if (fourCc.SequenceEqual("DXT3"u8))
        {
            format = TextureFormat.Dxt3;
        }
        else if (fourCc.SequenceEqual("DXT5"u8))
        {
            format = TextureFormat.Dxt5;
        }
        else if (BinaryPrimitives.ReadInt32LittleEndian(dds[88..]) == 32)
        {
            format = TextureFormat.Argb8888;
        }
        else
        {
            return false;
        }

        return width > 0 && height > 0;
    }
}
