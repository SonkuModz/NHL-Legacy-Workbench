using System.Text;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;

namespace NhlWorkbench.Core.Mods;

/// <summary>One texture from a game file, as a complete .dds file.</summary>
public sealed record ExportedDds(string Name, int Index, TextureFormat Format, int Width, int Height, int MipCount, byte[] Bytes)
{
    /// <summary>A safe file name: just the stem when the file holds one texture, otherwise numbered and named.</summary>
    public string FileName(string stem, bool isOnlyTexture)
    {
        if (isOnlyTexture)
        {
            return TextureDds.SafeFileName(stem) + ".dds";
        }

        string name = TextureDds.SafeFileName(Name);
        return $"{TextureDds.SafeFileName(stem)}_{Index:00}{(name.Length == 0 ? string.Empty : "_" + name)}.dds";
    }
}

/// <summary>
/// Moves textures between game files and .dds files without recompressing them when it can be avoided.
/// An exported DDS holds the game's own bytes, every mip included.
/// </summary>
public static class TextureDds
{
    /// <summary>Exports every texture in the file, smallest index first.</summary>
    public static Result<IReadOnlyList<ExportedDds>> ExportAll(byte[] rpsgl, string sourceName, DdsCodec codec)
    {
        Result<RpsglContainer> parsed = RpsglReader.Parse(rpsgl, sourceName);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<IReadOnlyList<ExportedDds>>(parsed.Error!, parsed.Exception);
        }

        var exported = new List<ExportedDds>();
        foreach (TextureEntry texture in parsed.Value!.Textures.OrderBy(t => t.Index))
        {
            Result<ExportedDds> one = Export(texture, codec);
            if (!one.IsSuccess)
            {
                return Result.Failure<IReadOnlyList<ExportedDds>>($"{sourceName}: {one.Error}", one.Exception);
            }

            exported.Add(one.Value!);
        }

        return exported.Count == 0
            ? Result.Failure<IReadOnlyList<ExportedDds>>($"{sourceName} has no textures to export.")
            : Result.Success<IReadOnlyList<ExportedDds>>(exported);
    }

    /// <summary>Exports one texture, or by default the one a picture would replace: the only one in the file, or the largest.</summary>
    public static Result<ExportedDds> ExportMain(byte[] rpsgl, string sourceName, DdsCodec codec, int? textureIndex = null)
    {
        Result<RpsglContainer> parsed = RpsglReader.Parse(rpsgl, sourceName);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<ExportedDds>(parsed.Error!, parsed.Exception);
        }

        TextureEntry? main = PictureSwap.FindTexture(parsed.Value!, textureIndex);
        return main is null
            ? Result.Failure<ExportedDds>($"{sourceName} has no textures to export.")
            : Export(main, codec);
    }

    /// <summary>True if the bytes start like a .dds file, whatever the file is called.</summary>
    public static bool IsDds(ReadOnlySpan<byte> bytes) => bytes.StartsWith("DDS "u8);

    /// <summary>
    /// Puts a .dds file into a texture (the main one unless <paramref name="textureIndex"/> says otherwise) and
    /// returns the whole new file. A DDS with the same format and size goes in untouched, apart from rebuilding
    /// the mips if it has too few or too many. Anything else is converted to fit, with a warning, since the
    /// file's layout can't change.
    /// </summary>
    public static Result<byte[]> Import(byte[] rpsgl, string sourceName, byte[] ddsBytes, DdsCodec codec, int? textureIndex = null)
    {
        Result<DecodedDdsTexture> read = codec.ReadDdsFile(ddsBytes);
        if (!read.IsSuccess)
        {
            return Result.Failure<byte[]>("That DDS can't be read. Save it as DXT1, DXT3 or DXT5 and try again.", read.Exception);
        }

        Result<RpsglContainer> parsed = RpsglReader.Parse(rpsgl, sourceName);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<byte[]>(parsed.Error!, parsed.Exception);
        }

        RpsglContainer container = parsed.Value!;
        TextureEntry? target = PictureSwap.FindTexture(container, textureIndex);
        if (target is null)
        {
            return Result.Failure<byte[]>($"{sourceName} has no textures to replace.");
        }

        DecodedDdsTexture dds = read.Value!;
        if (dds.Format != target.Format || dds.Width != target.Width || dds.Height != target.Height)
        {
            return ConvertAndReplace(rpsgl, sourceName, dds, target, codec);
        }

        Result<DecodedDdsTexture> matched = codec.AdjustMipCount(dds, target.MipCount);
        if (!matched.IsSuccess)
        {
            return Result.Failure<byte[]>(matched.Error!, matched.Exception);
        }

        container.ReplaceTexture(target.Index, new TextureEntry
        {
            Name = target.Name,
            Index = target.Index,
            Width = target.Width,
            Height = target.Height,
            Format = target.Format,
            MipCount = target.MipCount,
            LinearPixelData = matched.Value!.LinearData,
        });

        var warnings = new List<ParseWarning>();
        if (dds.MipCount != target.MipCount)
        {
            warnings.Add(new ParseWarning($"Your DDS had {dds.MipCount} mips and this texture needs {target.MipCount}, so the mips were adjusted to match.", sourceName));
        }

        return Result.Success(container.Save(PatchStrategy.InPlace), warnings);
    }

    internal static string SafeFileName(string name)
    {
        var safe = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return safe.ToString().Trim('.', '_');
    }

    private static Result<ExportedDds> Export(TextureEntry texture, DdsCodec codec)
    {
        // Some modded files claim more mips than their size allows, which a DDS can't hold.
        int mips = Math.Min(texture.MipCount, DdsCodec.MaxMipCount(texture.Width, texture.Height));
        Result<byte[]> wrapped = codec.WrapAsDds(texture.LinearPixelData, texture.Width, texture.Height, texture.Format, mips);
        return wrapped.IsSuccess
            ? Result.Success(new ExportedDds(texture.Name, texture.Index, texture.Format, texture.Width, texture.Height, mips, wrapped.Value!))
            : Result.Failure<ExportedDds>(wrapped.Error!, wrapped.Exception);
    }

    private static Result<byte[]> ConvertAndReplace(byte[] rpsgl, string sourceName, DecodedDdsTexture dds, TextureEntry target, DdsCodec codec)
    {
        Result<Rgba32Image> picture = codec.Decode(dds.LinearData, dds.Width, dds.Height, dds.Format);
        if (!picture.IsSuccess)
        {
            return Result.Failure<byte[]>("That DDS can't be read. Save it as DXT1, DXT3 or DXT5 and try again.", picture.Exception);
        }

        Result<byte[]> replaced = PictureSwap.Replace(rpsgl, sourceName, picture.Value!, codec, target.Index);
        if (!replaced.IsSuccess)
        {
            return replaced;
        }

        var warnings = new List<ParseWarning>
        {
            new($"Your DDS is {dds.Width} × {dds.Height} {dds.Format}, and this texture is {target.Width} × {target.Height} {target.Format}. It was converted to fit, which recompresses it.", sourceName),
        };
        warnings.AddRange(replaced.Warnings);
        return Result.Success(replaced.Value!, warnings);
    }
}
