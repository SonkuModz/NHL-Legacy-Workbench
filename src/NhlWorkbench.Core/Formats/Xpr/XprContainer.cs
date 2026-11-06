using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Xpr;

/// <summary>
/// Xbox 360 XPR2 packed-resource container -- the format wrapping the front-end UI
/// textures (team logos etc.) that live RefPack-compressed inside BIGF archives like
/// <c>fe/ion/artassets/teamlogosdynasty/d24.big</c>.
///
/// Layout ground-truthed against that real file's entry "96" (the St. Louis Blues logo)
/// and verified PIXEL-PERFECT (16381/16384 exact, remainder within PNG export rounding)
/// against NHL Modding Studio's independent decode of the same texture:
///   +0x00  "XPR2" magic
///   +0x04  header size (u32 BE)
///   +0x08  data size (u32 BE) -- for A8R8G8B8 this is exactly width*height*4, a
///          self-check this parser enforces
///   +0x0C  resource count (u32 BE)
///   +0x10  resource type tag, "TX2D" for a 2D texture
///   ...    resource/name table (not needed for the single-texture case)
///   +0x4F  pixel format code byte -- the SAME code table RX2 descriptors use
///          (<see cref="Rx2FormatCodes"/>): 0x86 = A8R8G8B8, 0x52..0x54 = DXT1/3/5
///   +0x50  packed size dword (u32 BE): width = (v &amp; 0x1FFF)+1, height = ((v>>13) &amp; 0x1FFF)+1
///          -- the standard X360 GPU fetch-constant 13-bit size encoding
///
/// Pixel data starts at <c>AlignUp(headerSize, 16)</c> -- NOT at headerSize itself
/// (discovered when every decode was off by exactly 3 texels = 12 bytes; the 16-byte
/// alignment also makes the buffer end land exactly at data end with zero slack).
///
/// Raw (A8R8G8B8) data uses EA's OWN simple tiling, NOT the XDK XGAddress2DTiledOffset
/// scheme (that produced a self-consistent but visibly woven/dashed image; so did
/// Noesis's native imageUntile360Raw, which matches XGAddress -- the XPR2 authoring
/// pipeline demonstrably used something simpler). The layout, solved mechanically from a
/// known-good reference image by anchoring uniquely-colored pixels and verifying the
/// same permutation repeats across all 16 macro tiles with zero conflicts:
///   - row-major 32x32-pixel macro tiles, 4096 bytes each;
///   - within a tile, for local pixel (lx, ly):
///       lxx   = lx ^ ((ly &amp; 8) &lt;&lt; 1)                  (x-bit4 flips when y-bit3 is set)
///       texel = (ly >> 1)*64 + (lxx >> 2)*8 + (ly &amp; 1)*4 + (lxx &amp; 3)
///     i.e. 4-pixel horizontal groups with even/odd row pairs interleaved group-wise;
///   - each 32-bit pixel is big-endian A,R,G,B; no 16-bit word swap.
/// Verified on a 128x128 texture (and visually across other team logos); the macro-tile
/// arrangement for non-multiple-of-32 dimensions is untested. DXT-coded XPR2 textures are
/// parsed via the swap16+block-untile path the RX2 reader was verified on, but no real
/// DXT XPR2 sample was available to confirm -- treat that path as best-effort.
/// </summary>
public sealed class XprContainer : ITextureContainer
{
    private const int HeaderSizeOffset = 0x04;
    private const int DataSizeOffset = 0x08;
    private const int ResourceTypeOffset = 0x10;
    private const int FormatCodeOffset = 0x4F;
    private const int PackedSizeOffset = 0x50;

    private readonly byte[] _decompressed;
    private readonly bool _wasRefPacked;
    private readonly int _dataStart;
    private readonly int _dataSize;
    private readonly FormatElementInfo _formatInfo;
    private TextureEntry _texture;
    private TextureEntry? _stagedReplacement;

    private XprContainer(byte[] decompressed, bool wasRefPacked, string sourcePath, int dataStart, int dataSize, FormatElementInfo formatInfo, TextureEntry texture)
    {
        _decompressed = decompressed;
        _wasRefPacked = wasRefPacked;
        SourcePath = sourcePath;
        _dataStart = dataStart;
        _dataSize = dataSize;
        _formatInfo = formatInfo;
        _texture = texture;
        Textures = new[] { texture };
    }

    public ContainerFormat Format => ContainerFormat.Xpr;

    public string SourcePath { get; }

    public IReadOnlyList<TextureEntry> Textures { get; private set; }

    public static bool IsXpr(byte[] data) =>
        data.Length >= 16 && data[0] == (byte)'X' && data[1] == (byte)'P' && data[2] == (byte)'R' && data[3] == (byte)'2';

    /// <summary>Accepts either a bare XPR2 buffer or a RefPack-compressed one (the form BIGF entries actually carry).</summary>
    public static Result<XprContainer> Parse(byte[] rawEntryBytes, string sourcePath)
    {
        try
        {
            bool wasRefPacked = RefPack.IsRefPack(rawEntryBytes);
            byte[] data = rawEntryBytes;
            if (wasRefPacked)
            {
                Result<byte[]> decompressed = RefPack.Decompress(rawEntryBytes);
                if (!decompressed.IsSuccess)
                {
                    return Result.Failure<XprContainer>(decompressed.Error ?? "RefPack decompression failed.");
                }

                data = decompressed.Value!;
            }

            if (!IsXpr(data))
            {
                return Result.Failure<XprContainer>("Not an XPR2 resource (magic mismatch).");
            }

            if (data.Length < PackedSizeOffset + 4)
            {
                return Result.Failure<XprContainer>("XPR2 buffer is too short to contain a texture descriptor.");
            }

            string resourceType = System.Text.Encoding.ASCII.GetString(data, ResourceTypeOffset, 4);
            if (resourceType != "TX2D")
            {
                return Result.Failure<XprContainer>($"Unsupported XPR2 resource type '{resourceType}' (only TX2D textures are supported).");
            }

            int headerSize = BigEndianReader.ReadInt32BEAt(data, HeaderSizeOffset);
            int dataSize = BigEndianReader.ReadInt32BEAt(data, DataSizeOffset);
            int dataStart = (headerSize + 15) & ~15; // pixel data is 16-byte aligned past the header
            if (headerSize <= 0 || dataSize <= 0 || dataStart + dataSize > data.Length)
            {
                return Result.Failure<XprContainer>($"XPR2 size fields are inconsistent (header {headerSize}, data {dataSize}, buffer {data.Length}).");
            }

            byte formatCode = data[FormatCodeOffset];
            if (!Rx2FormatCodes.TryGetInfo(formatCode, out FormatElementInfo formatInfo))
            {
                return Result.Failure<XprContainer>($"Unsupported XPR2 texture format byte 0x{formatCode:X2}.");
            }

            uint packedSize = BigEndianReader.ReadUInt32BEAt(data, PackedSizeOffset);
            int width = (int)(packedSize & 0x1FFF) + 1;
            int height = (int)((packedSize >> 13) & 0x1FFF) + 1;

            byte[] tiled = BufferUtilities.SafeSlice(data, dataStart, dataSize);
            byte[] linear;

            if (formatInfo.Layout == PixelLayout.CompressedBlock)
            {
                // Same conventions the RX2 reader was byte-verified on for DXT block data.
                int blocksWide = Math.Max(1, (width + 3) / 4);
                int blocksHigh = Math.Max(1, (height + 3) / 4);
                int mip0Size = blocksWide * blocksHigh * formatInfo.BytesPerElement;
                if (mip0Size > dataSize)
                {
                    return Result.Failure<XprContainer>($"XPR2 data region ({dataSize} bytes) is smaller than one {width}x{height} {formatInfo.Format} mip ({mip0Size} bytes).");
                }

                linear = X360Tiling.Untile(X360Tiling.SwapEndian16(tiled), width, height, formatInfo.BytesPerElement);
            }
            else
            {
                int expected = width * height * formatInfo.BytesPerElement;
                if (expected != dataSize)
                {
                    return Result.Failure<XprContainer>($"XPR2 size cross-check failed: {width}x{height}x{formatInfo.BytesPerElement} = {expected} but data size is {dataSize}.");
                }

                if (formatInfo.BytesPerElement != 4)
                {
                    return Result.Failure<XprContainer>($"Unverified raw XPR2 pixel size ({formatInfo.BytesPerElement} bytes/pixel) -- only 32-bit A8R8G8B8 has a verified layout.");
                }

                if ((width & 31) != 0 || (height & 31) != 0)
                {
                    return Result.Failure<XprContainer>($"XPR2 raw texture is {width}x{height}; only multiple-of-32 dimensions have a verified tile layout.");
                }

                linear = ArgbToBgra(EaXprUntileRawPixels(tiled, width, height));
            }

            var texture = new TextureEntry
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                Index = 0,
                Width = width,
                Height = height,
                Format = formatInfo.Format,
                MipCount = 1,
                LinearPixelData = linear,
            };

            return Result.Success(new XprContainer(data, wasRefPacked, sourcePath, dataStart, dataSize, formatInfo, texture));
        }
        catch (Exception ex)
        {
            return Result.Failure<XprContainer>($"Unhandled error parsing XPR2 resource: {ex.Message}", ex);
        }
    }

    public void ReplaceTexture(int textureIndex, TextureEntry replacement)
    {
        if (textureIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureIndex), textureIndex, "An XPR2 container only ever has one texture, at index 0.");
        }

        _stagedReplacement = replacement;
        _texture = replacement;
        Textures = new[] { replacement };
    }

    /// <summary>The pixel region is a fixed-size slot inside a fixed header layout, so only a
    /// same-shape (dimensions/format) replacement can be written back -- there is no rebuild
    /// path that could re-derive the descriptor for a different shape without more samples
    /// of this format than exist to verify against.</summary>
    public PatchCapabilities GetPatchCapabilities()
    {
        if (_stagedReplacement is null)
        {
            return PatchCapabilities.InPlace;
        }

        bool sameShape = _stagedReplacement.Width == OriginalWidth
            && _stagedReplacement.Height == OriginalHeight
            && _stagedReplacement.Format == _formatInfo.Format;

        return sameShape ? PatchCapabilities.InPlace : PatchCapabilities.None;
    }

    private int OriginalWidth => (int)(BigEndianReader.ReadUInt32BEAt(_decompressed, PackedSizeOffset) & 0x1FFF) + 1;

    private int OriginalHeight => (int)((BigEndianReader.ReadUInt32BEAt(_decompressed, PackedSizeOffset) >> 13) & 0x1FFF) + 1;

    public byte[] Save(PatchStrategy strategy)
    {
        if (strategy != PatchStrategy.InPlace || !GetPatchCapabilities().HasFlag(PatchCapabilities.InPlace))
        {
            throw new NotSupportedException(
                "XPR2 textures can only be replaced with identical dimensions and format "
                + $"({OriginalWidth}x{OriginalHeight} {_formatInfo.Format}).");
        }

        var patched = (byte[])_decompressed.Clone();

        byte[] tiled;
        if (_formatInfo.Layout == PixelLayout.CompressedBlock)
        {
            tiled = X360Tiling.SwapEndian16(X360Tiling.Tile(_texture.LinearPixelData, _texture.Width, _texture.Height, _formatInfo.BytesPerElement));
        }
        else
        {
            tiled = EaXprTileRawPixels(BgraToArgb(_texture.LinearPixelData), _texture.Width, _texture.Height);
        }

        Array.Copy(tiled, 0, patched, _dataStart, Math.Min(tiled.Length, _dataSize));

        if (!_wasRefPacked)
        {
            return patched;
        }

        Result<byte[]> compressed = RefPack.Compress(patched);
        if (!compressed.IsSuccess)
        {
            throw new InvalidOperationException(compressed.Error ?? "RefPack compression failed.");
        }

        return compressed.Value!;
    }

    /// <summary>The solved EA XPR2 raw-pixel tiling (see the class remarks for provenance):
    /// row-major 32x32 macro tiles; within a tile, 4-pixel groups with even/odd row pairs
    /// interleaved and an x-bit4 flip on rows where y-bit3 is set.</summary>
    private static int EaXprTexelOffset(int x, int y, int width)
    {
        int macroCols = width / 32;
        int tile = (y >> 5) * macroCols + (x >> 5);
        int ly = y & 31;
        int lx = (x & 31) ^ ((ly & 8) << 1);
        return tile * 1024 + ((ly >> 1) * 64) + ((lx >> 2) * 8) + ((ly & 1) * 4) + (lx & 3);
    }

    private static byte[] EaXprUntileRawPixels(byte[] tiled, int width, int height)
    {
        var linear = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int src = EaXprTexelOffset(x, y, width) * 4;
                if (src + 4 <= tiled.Length)
                {
                    Array.Copy(tiled, src, linear, (y * width + x) * 4, 4);
                }
            }
        }

        return linear;
    }

    private static byte[] EaXprTileRawPixels(byte[] linear, int width, int height)
    {
        var tiled = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dst = EaXprTexelOffset(x, y, width) * 4;
                if (dst + 4 <= tiled.Length)
                {
                    Array.Copy(linear, (y * width + x) * 4, tiled, dst, 4);
                }
            }
        }

        return tiled;
    }

    /// <summary>X360 stores each 32-bit raw pixel as big-endian A,R,G,B; <see cref="TextureFormat.Argb8888"/>
    /// maps to DXGI B8G8R8A8 whose memory order is B,G,R,A -- reorder once here so every
    /// downstream consumer (preview, DDS export, replacement) works unchanged.</summary>
    private static byte[] ArgbToBgra(byte[] argb)
    {
        var bgra = new byte[argb.Length];
        for (int i = 0; i + 4 <= argb.Length; i += 4)
        {
            bgra[i] = argb[i + 3];
            bgra[i + 1] = argb[i + 2];
            bgra[i + 2] = argb[i + 1];
            bgra[i + 3] = argb[i];
        }

        return bgra;
    }

    private static byte[] BgraToArgb(byte[] bgra)
    {
        var argb = new byte[bgra.Length];
        for (int i = 0; i + 4 <= bgra.Length; i += 4)
        {
            argb[i] = bgra[i + 3];
            argb[i + 1] = bgra[i + 2];
            argb[i + 2] = bgra[i + 1];
            argb[i + 3] = bgra[i];
        }

        return argb;
    }
}
