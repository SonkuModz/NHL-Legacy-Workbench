using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rpsgl;

namespace NhlWorkbench.Core.Mods;

/// <summary>Puts an ordinary picture into a game texture file without changing the file's layout.</summary>
public static class PictureSwap
{
    /// <summary>The texture a picture replaces. Files normally hold one; if not, it's the largest.</summary>
    public static TextureEntry? MainTexture(RpsglContainer container) =>
        container.Textures.MaxBy(t => (long)t.Width * t.Height);

    /// <summary>The texture with this index, or the main one if no index is given. Null if there isn't one.</summary>
    public static TextureEntry? FindTexture(RpsglContainer container, int? textureIndex) =>
        textureIndex is { } index ? container.Textures.FirstOrDefault(t => t.Index == index) : MainTexture(container);

    /// <summary>
    /// Fits <paramref name="picture"/> inside a texture (the main one unless <paramref name="textureIndex"/> says
    /// otherwise), keeping its proportions and leaving any spare space transparent, then encodes it in the
    /// texture's own format, size and mip count. Returns the whole new file. The file's size and every other
    /// byte outside the texture stay as they were.
    /// </summary>
    public static Result<byte[]> Replace(byte[] rpsgl, string sourceName, Rgba32Image picture, DdsCodec codec, int? textureIndex = null)
    {
        Result<RpsglContainer> parsed = RpsglReader.Parse(rpsgl, sourceName);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<byte[]>(parsed.Error!, parsed.Exception);
        }

        RpsglContainer container = parsed.Value!;
        TextureEntry? target = FindTexture(container, textureIndex);
        if (target is null)
        {
            return Result.Failure<byte[]>($"{sourceName} has no textures to replace.");
        }

        Result<Rgba32Image> fitted = codec.FitInside(picture, target.Width, target.Height);
        if (!fitted.IsSuccess)
        {
            return Result.Failure<byte[]>(fitted.Error!, fitted.Exception);
        }

        Result<DecodedDdsTexture> encoded = codec.EncodeToFormat(fitted.Value!, target.Format, target.MipCount);
        if (!encoded.IsSuccess)
        {
            return Result.Failure<byte[]>(encoded.Error!, encoded.Exception);
        }

        container.ReplaceTexture(target.Index, new TextureEntry
        {
            Name = target.Name,
            Index = target.Index,
            Width = target.Width,
            Height = target.Height,
            Format = target.Format,
            MipCount = target.MipCount,
            LinearPixelData = encoded.Value!.LinearData,
        });

        var warnings = new List<ParseWarning>();
        if (target.Format is TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked && HasSoftEdges(fitted.Value!))
        {
            warnings.Add(new ParseWarning("This texture's format only has hard-edged transparency, so soft edges will look rougher in game.", sourceName));
        }

        return Result.Success(container.Save(PatchStrategy.InPlace), warnings);
    }

    private static bool HasSoftEdges(Rgba32Image image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] is > 0 and < 255)
            {
                return true;
            }
        }

        return false;
    }
}
