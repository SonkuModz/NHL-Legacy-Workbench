using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Formats.Xpr;

namespace NhlWorkbench.Core.JerseySlots;

public enum SlotTextureFileKind
{
    /// <summary>A texture container (.rx2) -- jersey set, name plate, pants, socks.</summary>
    Rx2,

    /// <summary>The front-end menu picture: a .big UI package whose image entry is a RefPack'd XPR2 texture.</summary>
    MenuArt,
}

/// <summary>
/// One of a slot's files opened as "a list of textures that can be swapped": reads them, and builds the whole
/// file's new bytes with chosen textures replaced. For .rx2 files this is the same Rx2Container path the Jersey
/// Importer uses (in-place when the shape matches, otherwise a rebuild); for the menu .big only the picture
/// entry changes and every other entry is carried over byte-for-byte.
/// </summary>
public sealed class SlotTextureFile
{
    private readonly byte[] _bytes;

    private SlotTextureFile(string path, SlotTextureFileKind kind, byte[] bytes, IReadOnlyList<TextureEntry> textures, string? menuEntryName)
    {
        Path = path;
        Kind = kind;
        _bytes = bytes;
        Textures = textures;
        MenuEntryName = menuEntryName;
    }

    public string Path { get; }

    public SlotTextureFileKind Kind { get; }

    public IReadOnlyList<TextureEntry> Textures { get; }

    private string? MenuEntryName { get; }

    public static Result<SlotTextureFile> Load(string path) => Load(path, File.ReadAllBytes(path));

    public static Result<SlotTextureFile> Load(string path, byte[] bytes)
    {
        try
        {
            if (BigfArchive.IsBigf(bytes))
            {
                Result<BigfArchive> archive = BigfArchive.Parse(bytes, path);
                if (!archive.IsSuccess)
                {
                    return Result.Failure<SlotTextureFile>(archive.Error!);
                }

                foreach (BigfEntry entry in archive.Value!.Entries)
                {
                    byte[] data = archive.Value.GetEntryData(entry.Name);
                    if (BigEntryClassifier.Classify(data) != BigEntryFormat.Xpr)
                    {
                        continue;
                    }

                    Result<XprContainer> xpr = XprContainer.Parse(data, path);
                    if (xpr.IsSuccess)
                    {
                        return Result.Success(new SlotTextureFile(path, SlotTextureFileKind.MenuArt, bytes, xpr.Value!.Textures, entry.Name));
                    }
                }

                return Result.Failure<SlotTextureFile>("No picture was found inside this menu package.");
            }

            Result<Rx2Container> container = Rx2Reader.Parse(bytes, path);
            return container.IsSuccess
                ? Result.Success(new SlotTextureFile(path, SlotTextureFileKind.Rx2, bytes, container.Value!.Textures, null))
                : Result.Failure<SlotTextureFile>(container.Error!);
        }
        catch (Exception ex)
        {
            return Result.Failure<SlotTextureFile>($"Couldn't read '{System.IO.Path.GetFileName(path)}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The file's bytes with the given textures (by index) replaced. Nothing is returned on failure, and
    /// the result is re-read and checked before it's handed back.
    /// </summary>
    public Result<byte[]> Build(IReadOnlyDictionary<int, TextureEntry> replacements)
    {
        try
        {
            byte[] built = Kind == SlotTextureFileKind.Rx2 ? BuildRx2(replacements) : BuildMenuArt(replacements);

            Result<SlotTextureFile> check = Load(Path, built);
            if (!check.IsSuccess || check.Value!.Textures.Count != Textures.Count)
            {
                return Result.Failure<byte[]>($"The edited '{System.IO.Path.GetFileName(Path)}' didn't read back correctly, so it wasn't built.");
            }

            foreach ((int index, TextureEntry replacement) in replacements)
            {
                TextureEntry back = check.Value.Textures[index];
                if (back.Width != replacement.Width || back.Height != replacement.Height || back.Format != replacement.Format
                    || !back.LinearPixelData.AsSpan(0, Math.Min(back.LinearPixelData.Length, replacement.LinearPixelData.Length)).SequenceEqual(replacement.LinearPixelData.AsSpan(0, Math.Min(back.LinearPixelData.Length, replacement.LinearPixelData.Length))))
                {
                    return Result.Failure<byte[]>($"Texture '{replacement.Name}' didn't read back identically after being written, so the file wasn't built.");
                }
            }

            return Result.Success(built);
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"Couldn't write '{System.IO.Path.GetFileName(Path)}': {ex.Message}", ex);
        }
    }

    private byte[] BuildRx2(IReadOnlyDictionary<int, TextureEntry> replacements)
    {
        Rx2Container container = Rx2Reader.Parse(_bytes, Path).Value!;
        foreach ((int index, TextureEntry replacement) in replacements)
        {
            container.ReplaceTexture(index, replacement);
        }

        PatchStrategy strategy = container.GetPatchCapabilities().HasFlag(PatchCapabilities.InPlace) ? PatchStrategy.InPlace : PatchStrategy.Rebuild;
        return container.Save(strategy);
    }

    private byte[] BuildMenuArt(IReadOnlyDictionary<int, TextureEntry> replacements)
    {
        BigfArchive archive = BigfArchive.Parse(_bytes, Path).Value!;
        XprContainer xpr = XprContainer.Parse(archive.GetEntryData(MenuEntryName!), Path).Value!;
        foreach ((int index, TextureEntry replacement) in replacements)
        {
            xpr.ReplaceTexture(index, replacement);
        }

        // XPR2 only takes a same-shape replacement (its Save throws otherwise); the caller converts to the original shape first.
        byte[] entryBytes = xpr.Save(PatchStrategy.InPlace);
        return archive.RebuildReplacingEntry(MenuEntryName!, entryBytes);
    }
}

/// <summary>Turns a user-chosen file into a <see cref="TextureEntry"/> that can replace an existing texture.</summary>
public static class SlotTextureReplacer
{
    public sealed record Result(TextureEntry Entry, string? Note, bool ShapeChanged);

    /// <summary>
    /// <paramref name="isDds"/> files keep their own format when <paramref name="allowShapeChange"/> (a container that can be
    /// rebuilt); everything else -- PNG/JPG/BMP, or a DDS going into a fixed-shape slot -- is resized to the original's size
    /// and re-encoded into the original's format with its original mip count.
    /// </summary>
    public static Diagnostics.Result<Result> Build(DdsCodec codec, TextureEntry original, byte[] fileBytes, bool isDds, bool allowShapeChange)
    {
        try
        {
            if (isDds)
            {
                Diagnostics.Result<DecodedDdsTexture> read = codec.ReadDdsFile(fileBytes);
                if (!read.IsSuccess)
                {
                    return Diagnostics.Result.Failure<Result>(read.Error!);
                }

                DecodedDdsTexture decoded = read.Value!;
                string? note = null;
                bool sameSize = decoded.Width == original.Width && decoded.Height == original.Height;
                if (decoded.MipCount != original.MipCount && (sameSize || !allowShapeChange))
                {
                    // The recompiled engine hard-codes each texture's mip count, so match the original (same rule the Jersey Importer follows).
                    Diagnostics.Result<DecodedDdsTexture> adjusted = codec.AdjustMipCount(decoded, original.MipCount);
                    if (!adjusted.IsSuccess)
                    {
                        return Diagnostics.Result.Failure<Result>(adjusted.Error!);
                    }

                    note = $"Mip levels adjusted from {decoded.MipCount} to {original.MipCount} to match the original.";
                    decoded = adjusted.Value!;
                }

                if (decoded.MipCount != original.MipCount && !sameSize && allowShapeChange)
                {
                    note = $"Different size: its {decoded.MipCount} mip level(s) were kept (the original has {original.MipCount}); the engine may expect the original count.";
                }

                bool sameShape = decoded.Width == original.Width && decoded.Height == original.Height && decoded.Format == original.Format;
                if (sameShape || allowShapeChange)
                {
                    return Diagnostics.Result.Success(new Result(ToEntry(original, decoded), note, !sameShape));
                }

                // Fixed-shape slot and the DDS isn't that shape: decode it and re-encode below.
                Diagnostics.Result<Rgba32Image> decodedImage = codec.Decode(decoded.LinearData, decoded.Width, decoded.Height, decoded.Format);
                if (!decodedImage.IsSuccess)
                {
                    return Diagnostics.Result.Failure<Result>(decodedImage.Error!);
                }

                return Reencode(codec, original, decodedImage.Value!, $"Converted from {decoded.Width}x{decoded.Height} {decoded.Format}");
            }

            Diagnostics.Result<Rgba32Image> image = codec.DecodeImageFile(fileBytes);
            return image.IsSuccess
                ? Reencode(codec, original, image.Value!, "Picture")
                : Diagnostics.Result.Failure<Result>(image.Error!);
        }
        catch (Exception ex)
        {
            return Diagnostics.Result.Failure<Result>($"Couldn't use that file: {ex.Message}", ex);
        }
    }

    private static Diagnostics.Result<Result> Reencode(DdsCodec codec, TextureEntry original, Rgba32Image image, string what)
    {
        string note = image.Width == original.Width && image.Height == original.Height
            ? $"{what} encoded to {original.Format}."
            : $"{what} ({image.Width}x{image.Height}) resized to {original.Width}x{original.Height} and encoded to {original.Format}.";
        Rgba32Image sized = ImageResampler.Resize(image, original.Width, original.Height);
        Diagnostics.Result<DecodedDdsTexture> encoded = codec.EncodeToFormat(sized, original.Format, original.MipCount);
        return encoded.IsSuccess
            ? Diagnostics.Result.Success(new Result(ToEntry(original, encoded.Value!), note, false))
            : Diagnostics.Result.Failure<Result>(encoded.Error!);
    }

    private static TextureEntry ToEntry(TextureEntry original, DecodedDdsTexture decoded) => new()
    {
        Name = original.Name,
        Index = original.Index,
        Width = decoded.Width,
        Height = decoded.Height,
        Format = decoded.Format,
        MipCount = decoded.MipCount,
        LinearPixelData = decoded.LinearData,
    };
}
