using System.Runtime.InteropServices;
using DirectXTexNet;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Dds;

/// <summary>
/// Facade over DirectXTexNet (an in-process managed wrapper around Microsoft's
/// DirectXTex). Callers only ever see <see cref="Rgba32Image"/>,
/// <see cref="DdsCompressionFormat"/>, <see cref="DdsConversionOptions"/>, and
/// <see cref="Result{T}"/> -- DirectXTexNet types never cross this boundary, so if its
/// native interop ever proves unreliable, swapping the implementation (e.g. shelling out
/// to texconv.exe, which is proven to work per the reference prototype) is a contained
/// change behind this class, not an API change for callers.
/// </summary>
public sealed class DdsCodec
{
    /// <summary>
    /// Decodes a texture's pixel data to RGBA8 for preview/export. <paramref name="linearPixelData"/>
    /// may be either just the base (mip 0) bytes or the whole concatenated mip chain --
    /// only the base level's worth of bytes is read.
    /// </summary>
    public Result<Rgba32Image> Decode(byte[] linearPixelData, int width, int height, TextureFormat format)
    {
        try
        {
            DXGI_FORMAT sourceFormat = ToDxgiFormat(format);
            int mip0Size = ComputeMip0Size(width, height, format);
            byte[] mip0 = BufferUtilities.SafeSlice(linearPixelData, 0, mip0Size);

            using ScratchImage source = TexHelper.Instance.Initialize2D(sourceFormat, width, height, 1, 1, CP_FLAGS.NONE);
            CopyInto(source.GetImage(0), mip0);

            using ScratchImage rgba = TexHelper.Instance.IsCompressed(sourceFormat)
                ? source.Decompress(DXGI_FORMAT.R8G8B8A8_UNORM)
                : source.Convert(DXGI_FORMAT.R8G8B8A8_UNORM, TEX_FILTER_FLAGS.DEFAULT, 0.5f);

            Image image = rgba.GetImage(0);
            byte[] pixels = CopyOut(image);

            return Result.Success(new Rgba32Image { Width = image.Width, Height = image.Height, Pixels = pixels });
        }
        catch (Exception ex)
        {
            return Result.Failure<Rgba32Image>($"Failed to decode texture: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decodes the smallest mip that's still at least <paramref name="minimumSize"/> pixels across, which is far
    /// quicker than decoding the full picture when only a thumbnail is needed.
    /// </summary>
    public Result<Rgba32Image> DecodeForPreview(byte[] linearMipChainData, int width, int height, TextureFormat format, int mipCount, int minimumSize)
    {
        mipCount = Math.Clamp(mipCount, 1, MaxMipCount(width, height));
        int offset = 0;
        for (int mip = 0; mip < mipCount - 1; mip++)
        {
            int mipWidth = Math.Max(1, width >> mip);
            int mipHeight = Math.Max(1, height >> mip);
            int nextWidth = Math.Max(1, width >> (mip + 1));
            int nextHeight = Math.Max(1, height >> (mip + 1));
            if (Math.Max(nextWidth, nextHeight) < minimumSize)
            {
                return DecodeSlice(linearMipChainData, offset, mipWidth, mipHeight, format);
            }

            offset += ComputeMip0Size(mipWidth, mipHeight, format);
        }

        return DecodeSlice(linearMipChainData, offset, Math.Max(1, width >> Math.Max(0, mipCount - 1)), Math.Max(1, height >> Math.Max(0, mipCount - 1)), format);
    }

    /// <summary>The most mip levels a texture of this size can have, down to 1 × 1.</summary>
    public static int MaxMipCount(int width, int height) =>
        System.Numerics.BitOperations.Log2((uint)Math.Max(1, Math.Max(width, height))) + 1;

    private Result<Rgba32Image> DecodeSlice(byte[] chain, int offset, int width, int height, TextureFormat format)
    {
        int size = ComputeMip0Size(width, height, format);
        return offset + size > chain.Length
            ? Result.Failure<Rgba32Image>("The texture's mips are shorter than its header says.")
            : Decode(chain.AsSpan(offset, size).ToArray(), width, height, format);
    }

    /// <summary>
    /// Reads a standalone .dds file (as picked by the user for a Texture Editor
    /// "Replace" operation) and returns its format/dimensions/mip count plus the full
    /// mip chain as concatenated raw block bytes -- no DDS header, directly usable as a
    /// <c>TextureEntry.LinearPixelData</c> replacement.
    /// </summary>
    public Result<DecodedDdsTexture> ReadDdsFile(byte[] ddsBytes)
    {
        GCHandle handle = GCHandle.Alloc(ddsBytes, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = handle.AddrOfPinnedObject();
            using ScratchImage image = TexHelper.Instance.LoadFromDDSMemory(pointer, ddsBytes.Length, DDS_FLAGS.NONE);
            TexMetadata metadata = image.GetMetadata();

            if (!TryFromDxgiFormat(metadata.Format, out TextureFormat format))
            {
                return Result.Failure<DecodedDdsTexture>($"Unsupported DDS pixel format: {metadata.Format}.");
            }

            using var buffer = new MemoryStream();
            for (int mip = 0; mip < metadata.MipLevels; mip++)
            {
                byte[] mipBytes = CopyOut(image.GetImage(mip, 0, 0));
                buffer.Write(mipBytes);
            }

            return Result.Success(new DecodedDdsTexture(format, metadata.Width, metadata.Height, metadata.MipLevels, buffer.ToArray()));
        }
        catch (Exception ex)
        {
            return Result.Failure<DecodedDdsTexture>($"Failed to read DDS file: {ex.Message}", ex);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Wraps a texture's already-compressed/raw mip chain in a standard DDS header
    /// without any decompress/recompress round-trip -- this is the lossless default
    /// export path for a texture whose native container format already matches a DDS-
    /// representable format (the common case for RX2/RPSGL textures).
    /// </summary>
    public Result<byte[]> WrapAsDds(byte[] linearMipChainData, int width, int height, TextureFormat format, int mipCount)
    {
        try
        {
            mipCount = Math.Max(1, mipCount);
            DXGI_FORMAT dxgiFormat = ToDxgiFormat(format);
            using ScratchImage image = TexHelper.Instance.Initialize2D(dxgiFormat, width, height, 1, mipCount, CP_FLAGS.NONE);

            int cursor = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int mipWidth = Math.Max(1, width >> m);
                int mipHeight = Math.Max(1, height >> m);
                int mipSize = ComputeMip0Size(mipWidth, mipHeight, format);
                byte[] mipBytes = BufferUtilities.SafeSlice(linearMipChainData, cursor, mipSize);
                CopyInto(image.GetImage(m, 0, 0), mipBytes);
                cursor += mipSize;
            }

            using UnmanagedMemoryStream ddsStream = image.SaveToDDSMemory(DDS_FLAGS.NONE);
            byte[] result = ReadFully(ddsStream);
            return Result.Success(result);
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"Failed to wrap texture as DDS: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Re-derives a decoded DDS's mip chain to have exactly <paramref name="targetMipCount"/>
    /// levels, keeping mip 0 (and thus the visible full-res image) untouched. Shrinking just
    /// keeps the first N levels already present; growing decompresses mip 0, regenerates a
    /// fresh chain, and recompresses back to the source format. Needed because some engines
    /// (recompiled/emulated builds in particular) bake in the *original* texture's mip count
    /// rather than reading it from the file at runtime -- a replacement with a different mip
    /// count than the texture it's replacing can decode and preview fine in this app while
    /// still rendering corrupted in that engine, since the app always reads mip count
    /// dynamically from the file but the engine may not.
    /// </summary>
    public Result<DecodedDdsTexture> AdjustMipCount(DecodedDdsTexture source, int targetMipCount)
    {
        targetMipCount = Math.Max(1, targetMipCount);
        if (targetMipCount == source.MipCount)
        {
            return Result.Success(source);
        }

        try
        {
            if (targetMipCount < source.MipCount)
            {
                using var trimmed = new MemoryStream();
                int cursor = 0;
                for (int m = 0; m < targetMipCount; m++)
                {
                    int mipWidth = Math.Max(1, source.Width >> m);
                    int mipHeight = Math.Max(1, source.Height >> m);
                    int mipSize = ComputeMip0Size(mipWidth, mipHeight, source.Format);
                    trimmed.Write(BufferUtilities.SafeSlice(source.LinearData, cursor, mipSize));
                    cursor += mipSize;
                }

                return Result.Success(new DecodedDdsTexture(source.Format, source.Width, source.Height, targetMipCount, trimmed.ToArray()));
            }

            DXGI_FORMAT dxgiFormat = ToDxgiFormat(source.Format);
            int mip0Size = ComputeMip0Size(source.Width, source.Height, source.Format);
            byte[] mip0 = BufferUtilities.SafeSlice(source.LinearData, 0, mip0Size);

            using ScratchImage single = TexHelper.Instance.Initialize2D(dxgiFormat, source.Width, source.Height, 1, 1, CP_FLAGS.NONE);
            CopyInto(single.GetImage(0), mip0);

            bool isCompressed = TexHelper.Instance.IsCompressed(dxgiFormat);
            using ScratchImage rgba = isCompressed
                ? single.Decompress(DXGI_FORMAT.R8G8B8A8_UNORM)
                : single.Convert(DXGI_FORMAT.R8G8B8A8_UNORM, TEX_FILTER_FLAGS.DEFAULT, 0.5f);

            using ScratchImage mipped = rgba.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, targetMipCount);
            using ScratchImage final = isCompressed
                ? mipped.Compress(dxgiFormat, TEX_COMPRESS_FLAGS.DEFAULT | TEX_COMPRESS_FLAGS.PARALLEL, 0.5f)
                : mipped.Convert(dxgiFormat, TEX_FILTER_FLAGS.DEFAULT, 0.5f);

            using var grown = new MemoryStream();
            for (int m = 0; m < targetMipCount; m++)
            {
                grown.Write(CopyOut(final.GetImage(m, 0, 0)));
            }

            return Result.Success(new DecodedDdsTexture(source.Format, source.Width, source.Height, targetMipCount, grown.ToArray()));
        }
        catch (Exception ex)
        {
            return Result.Failure<DecodedDdsTexture>($"Failed to adjust mip count: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Compresses an already-decoded RGBA8 image directly to <paramref name="format"/> and
    /// generates a full <paramref name="mipCount"/>-level chain, returning linear (concatenated,
    /// uncompressed-header) mip-chain bytes ready for a <c>TextureEntry</c>. Exists because
    /// <see cref="EncodeToDds"/> only targets the DDS Converter's user-facing
    /// <see cref="DdsCompressionFormat"/> set (no BC5), whereas this uses the same
    /// <see cref="ToDxgiFormat(TextureFormat)"/> mapping <see cref="Decode"/>/<see cref="AdjustMipCount"/>
    /// already rely on -- which does cover <see cref="TextureFormat.Bc5NormalMap"/> -- so any
    /// texture format this app can already decode, it can also re-encode back to.
    /// </summary>
    public Result<DecodedDdsTexture> EncodeToFormat(Rgba32Image image, TextureFormat format, int mipCount)
    {
        mipCount = Math.Max(1, mipCount);
        try
        {
            DXGI_FORMAT dxgiFormat = ToDxgiFormat(format);

            using ScratchImage rgba = TexHelper.Instance.Initialize2D(DXGI_FORMAT.R8G8B8A8_UNORM, image.Width, image.Height, 1, 1, CP_FLAGS.NONE);
            CopyInto(rgba.GetImage(0), image.Pixels);

            // DirectXTex rejects a request for exactly one level, so a single-mip target just uses the base image.
            using ScratchImage? generated = mipCount > 1 ? rgba.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, mipCount) : null;
            ScratchImage mipped = generated ?? rgba;

            bool isCompressed = TexHelper.Instance.IsCompressed(dxgiFormat);
            using ScratchImage final = isCompressed
                ? mipped.Compress(dxgiFormat, TEX_COMPRESS_FLAGS.DEFAULT | TEX_COMPRESS_FLAGS.PARALLEL, 0.5f)
                : mipped.Convert(dxgiFormat, TEX_FILTER_FLAGS.DEFAULT, 0.5f);

            using var chain = new MemoryStream();
            for (int m = 0; m < mipCount; m++)
            {
                chain.Write(CopyOut(final.GetImage(m, 0, 0)));
            }

            return Result.Success(new DecodedDdsTexture(format, image.Width, image.Height, mipCount, chain.ToArray()));
        }
        catch (Exception ex)
        {
            return Result.Failure<DecodedDdsTexture>($"Failed to encode texture to {format}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Scales a picture to fit inside the given size, keeping its proportions and centering it on a
    /// transparent background. Transparent pixels take on the picture's average color, so the mip chain
    /// built from the result doesn't get dark fringes around its edges.
    /// </summary>
    public Result<Rgba32Image> FitInside(Rgba32Image image, int width, int height)
    {
        if (image.Width <= 0 || image.Height <= 0 || width <= 0 || height <= 0)
        {
            return Result.Failure<Rgba32Image>("The picture has no pixels.");
        }

        try
        {
            double scale = Math.Min((double)width / image.Width, (double)height / image.Height);
            int newWidth = Math.Clamp((int)Math.Round(image.Width * scale), 1, width);
            int newHeight = Math.Clamp((int)Math.Round(image.Height * scale), 1, height);

            byte[] scaled;
            if (newWidth == image.Width && newHeight == image.Height)
            {
                scaled = image.Pixels;
            }
            else
            {
                // Scaling premultiplied keeps the color of transparent pixels from bleeding into the edges.
                using ScratchImage source = TexHelper.Instance.Initialize2D(DXGI_FORMAT.R8G8B8A8_UNORM, image.Width, image.Height, 1, 1, CP_FLAGS.NONE);
                CopyInto(source.GetImage(0), Premultiply(image.Pixels));
                TEX_FILTER_FLAGS filter = scale < 1 ? TEX_FILTER_FLAGS.FANT : TEX_FILTER_FLAGS.CUBIC;
                using ScratchImage resized = source.Resize(newWidth, newHeight, filter);
                scaled = Unpremultiply(CopyOut(resized.GetImage(0)));
            }

            var canvas = new byte[width * height * 4];
            int left = (width - newWidth) / 2;
            int top = (height - newHeight) / 2;
            for (int row = 0; row < newHeight; row++)
            {
                Buffer.BlockCopy(scaled, row * newWidth * 4, canvas, ((top + row) * width + left) * 4, newWidth * 4);
            }

            FillTransparentWithAverageColor(canvas);
            return Result.Success(new Rgba32Image { Width = width, Height = height, Pixels = canvas });
        }
        catch (Exception ex)
        {
            return Result.Failure<Rgba32Image>($"Failed to fit picture: {ex.Message}", ex);
        }
    }

    private static byte[] Premultiply(byte[] rgba)
    {
        var result = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            result[i] = (byte)((rgba[i] * alpha + 127) / 255);
            result[i + 1] = (byte)((rgba[i + 1] * alpha + 127) / 255);
            result[i + 2] = (byte)((rgba[i + 2] * alpha + 127) / 255);
            result[i + 3] = (byte)alpha;
        }

        return result;
    }

    private static byte[] Unpremultiply(byte[] rgba)
    {
        var result = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            if (alpha > 0)
            {
                result[i] = (byte)Math.Min(255, (rgba[i] * 255 + (alpha / 2)) / alpha);
                result[i + 1] = (byte)Math.Min(255, (rgba[i + 1] * 255 + (alpha / 2)) / alpha);
                result[i + 2] = (byte)Math.Min(255, (rgba[i + 2] * 255 + (alpha / 2)) / alpha);
                result[i + 3] = (byte)alpha;
            }
        }

        return result;
    }

    private static void FillTransparentWithAverageColor(byte[] rgba)
    {
        long red = 0, green = 0, blue = 0, weight = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            red += rgba[i] * alpha;
            green += rgba[i + 1] * alpha;
            blue += rgba[i + 2] * alpha;
            weight += alpha;
        }

        if (weight == 0)
        {
            return;
        }

        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0)
            {
                rgba[i] = (byte)(red / weight);
                rgba[i + 1] = (byte)(green / weight);
                rgba[i + 2] = (byte)(blue / weight);
            }
        }
    }

    /// <summary>Decodes a standalone image file (PNG/JPEG/BMP/TIFF, via WIC) to RGBA8 -- the DDS Converter's single/batch source format.</summary>
    public Result<Rgba32Image> DecodeImageFile(byte[] fileBytes)
    {
        GCHandle handle = GCHandle.Alloc(fileBytes, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = handle.AddrOfPinnedObject();
            using ScratchImage source = TexHelper.Instance.LoadFromWICMemory(pointer, fileBytes.Length, WIC_FLAGS.NONE);

            // WIC hands PNGs back as an *_SRGB format, and DirectXTex then "helpfully" runs the sRGB->linear curve inside
            // Convert(), which darkened every imported picture (a PNG green of 220 arrived as 183). The picture's numbers are
            // what we want to keep -- the game's textures store them as-is -- so reinterpret as plain UNORM first.
            DXGI_FORMAT sourceFormat = source.GetMetadata().Format;
            if (TexHelper.Instance.IsSRGB(sourceFormat))
            {
                source.OverrideFormat(TexHelper.Instance.MakeLinear(sourceFormat));
            }

            // WIC frequently decodes PNG/BMP straight to R8G8B8A8_UNORM already; DirectXTex's
            // Convert() throws (E_INVALIDARG) when asked to "convert" a format to itself, so
            // only convert when the source format actually differs.
            if (source.GetMetadata().Format == DXGI_FORMAT.R8G8B8A8_UNORM)
            {
                Image direct = source.GetImage(0);
                return Result.Success(new Rgba32Image { Width = direct.Width, Height = direct.Height, Pixels = CopyOut(direct) });
            }

            using ScratchImage rgba = source.Convert(DXGI_FORMAT.R8G8B8A8_UNORM, TEX_FILTER_FLAGS.DEFAULT, 0.5f);
            Image image = rgba.GetImage(0);
            return Result.Success(new Rgba32Image { Width = image.Width, Height = image.Height, Pixels = CopyOut(image) });
        }
        catch (Exception ex)
        {
            return Result.Failure<Rgba32Image>($"Failed to decode image: {ex.Message}", ex);
        }
        finally
        {
            handle.Free();
        }
    }

    public Result<byte[]> EncodeToDds(Rgba32Image sourceImage, DdsCompressionFormat format, DdsConversionOptions options)
    {
        var disposables = new List<ScratchImage>();
        try
        {
            using ScratchImage rgba = TexHelper.Instance.Initialize2D(
                DXGI_FORMAT.R8G8B8A8_UNORM, sourceImage.Width, sourceImage.Height, 1, 1, CP_FLAGS.NONE);
            byte[] pixels = options.PreserveAlpha ? sourceImage.Pixels : ForceOpaque(sourceImage.Pixels);
            CopyInto(rgba.GetImage(0), pixels);

            ScratchImage current = rgba;

            if (options.ResizeTo is { } size)
            {
                ScratchImage resized = current.Resize(size.Width, size.Height, TEX_FILTER_FLAGS.DEFAULT);
                disposables.Add(resized);
                current = resized;
            }

            if (options.GenerateMipmaps)
            {
                ScratchImage mipped = current.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
                disposables.Add(mipped);
                current = mipped;
            }

            DXGI_FORMAT targetFormat = ToDxgiFormat(format);
            ScratchImage compressed = current.Compress(targetFormat, TEX_COMPRESS_FLAGS.DEFAULT | TEX_COMPRESS_FLAGS.PARALLEL, 0.5f);
            disposables.Add(compressed);

            using UnmanagedMemoryStream ddsStream = compressed.SaveToDDSMemory(DDS_FLAGS.NONE);
            return Result.Success(ReadFully(ddsStream));
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"Failed to encode DDS: {ex.Message}", ex);
        }
        finally
        {
            foreach (ScratchImage image in disposables)
            {
                image.Dispose();
            }
        }
    }

    private static int ComputeMip0Size(int width, int height, TextureFormat format)
    {
        (int bytesPerElement, bool compressedBlock) = ElementInfo(format);
        if (compressedBlock)
        {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            return blocksWide * blocksHigh * bytesPerElement;
        }

        return width * height * bytesPerElement;
    }

    private static (int BytesPerElement, bool CompressedBlock) ElementInfo(TextureFormat format) => format switch
    {
        TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked => (8, true),
        TextureFormat.Dxt2 or TextureFormat.Dxt3 => (16, true),
        TextureFormat.Dxt4 or TextureFormat.Dxt5 => (16, true),
        TextureFormat.Bc5NormalMap => (16, true),
        TextureFormat.Bc7 => (16, true),
        TextureFormat.Argb8888 => (4, false),
        TextureFormat.A1R5G5B5 or TextureFormat.B5G6R5 => (2, false),
        _ => (4, false),
    };

    private static bool TryFromDxgiFormat(DXGI_FORMAT format, out TextureFormat result)
    {
        TextureFormat? mapped = format switch
        {
            DXGI_FORMAT.BC1_UNORM or DXGI_FORMAT.BC1_UNORM_SRGB => TextureFormat.Dxt1,
            DXGI_FORMAT.BC2_UNORM or DXGI_FORMAT.BC2_UNORM_SRGB => TextureFormat.Dxt3,
            DXGI_FORMAT.BC3_UNORM or DXGI_FORMAT.BC3_UNORM_SRGB => TextureFormat.Dxt5,
            DXGI_FORMAT.BC5_UNORM or DXGI_FORMAT.BC5_SNORM => TextureFormat.Bc5NormalMap,
            DXGI_FORMAT.BC7_UNORM or DXGI_FORMAT.BC7_UNORM_SRGB => TextureFormat.Bc7,
            DXGI_FORMAT.B8G8R8A8_UNORM => TextureFormat.Argb8888,
            DXGI_FORMAT.B5G5R5A1_UNORM => TextureFormat.A1R5G5B5,
            DXGI_FORMAT.B5G6R5_UNORM => TextureFormat.B5G6R5,
            _ => null,
        };

        result = mapped ?? default;
        return mapped.HasValue;
    }

    private static byte[] ReadFully(UnmanagedMemoryStream stream)
    {
        var result = new byte[stream.Length];
        int totalRead = 0;
        while (totalRead < result.Length)
        {
            int read = stream.Read(result, totalRead, result.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return result;
    }

    private static void CopyInto(Image image, byte[] source)
    {
        int length = (int)Math.Min(source.LongLength, image.SlicePitch);
        if (length > 0)
        {
            Marshal.Copy(source, 0, image.Pixels, length);
        }
    }

    private static byte[] CopyOut(Image image)
    {
        var result = new byte[image.SlicePitch];
        Marshal.Copy(image.Pixels, result, 0, result.Length);
        return result;
    }

    private static byte[] ForceOpaque(byte[] rgba)
    {
        var copy = (byte[])rgba.Clone();
        for (int i = 3; i < copy.Length; i += 4)
        {
            copy[i] = 0xFF;
        }

        return copy;
    }

    private static DXGI_FORMAT ToDxgiFormat(TextureFormat format) => format switch
    {
        TextureFormat.Dxt1 or TextureFormat.Dxt1NormalPacked => DXGI_FORMAT.BC1_UNORM,
        TextureFormat.Dxt2 or TextureFormat.Dxt3 => DXGI_FORMAT.BC2_UNORM,
        TextureFormat.Dxt4 or TextureFormat.Dxt5 => DXGI_FORMAT.BC3_UNORM,
        TextureFormat.Bc5NormalMap => DXGI_FORMAT.BC5_UNORM,
        TextureFormat.Bc7 => DXGI_FORMAT.BC7_UNORM,
        TextureFormat.Argb8888 => DXGI_FORMAT.B8G8R8A8_UNORM,
        TextureFormat.A1R5G5B5 => DXGI_FORMAT.B5G5R5A1_UNORM,
        TextureFormat.B5G6R5 => DXGI_FORMAT.B5G6R5_UNORM,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format."),
    };

    private static DXGI_FORMAT ToDxgiFormat(DdsCompressionFormat format) => format switch
    {
        DdsCompressionFormat.Dxt1 => DXGI_FORMAT.BC1_UNORM,
        DdsCompressionFormat.Dxt2 or DdsCompressionFormat.Dxt3 => DXGI_FORMAT.BC2_UNORM,
        DdsCompressionFormat.Dxt4 or DdsCompressionFormat.Dxt5 => DXGI_FORMAT.BC3_UNORM,
        DdsCompressionFormat.Bc7 => DXGI_FORMAT.BC7_UNORM,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported DDS compression format."),
    };
}
