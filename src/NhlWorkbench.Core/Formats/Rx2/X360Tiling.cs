using System.Numerics;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Xbox 360 textures are stored as 32x32-DXT-block macro-tiles, with blocks inside
/// each macro-tile ordered by a Morton(Z-order)-style bit-interleave that differs
/// between 8-byte blocks (DXT1/BC1) and 16-byte blocks (DXT3/DXT5/BC5), plus the whole
/// blob is stored as big-endian 16-bit words.
///
/// This is the highest-risk code in the whole Core library: the bit formulas below are
/// a direct, line-for-line port of a working reference implementation (validated
/// against real game files) and must not be "cleaned up" without re-validating against
/// the round-trip tests in NhlWorkbench.Core.Tests -- a single swapped bit silently
/// corrupts every non-trivial texture without throwing.
/// </summary>
public static class X360Tiling
{
    /// <summary>Decodes a 10-bit Morton "inner" index (position within a 32x32 macro-tile) to local (lx, ly) block coordinates.</summary>
    public static (int Lx, int Ly) InnerToLxy(int inner, int logBpb)
    {
        int Bit(int v, int n) => (v >> n) & 1;

        int lx, ly;
        if (logBpb == 3)
        {
            // DXT1 / BC1 (8 bytes per block)
            lx = Bit(inner, 0) | (Bit(inner, 2) << 1) | (Bit(inner, 5) << 2) | (Bit(inner, 3) << 3) |
                 ((Bit(inner, 4) ^ Bit(inner, 9)) << 4);
            ly = Bit(inner, 1) | (Bit(inner, 6) << 1) | (Bit(inner, 7) << 2) | (Bit(inner, 9) << 3) |
                 (Bit(inner, 8) << 4);
        }
        else
        {
            // DXT3 / DXT5 / BC5 (16 bytes per block)
            lx = Bit(inner, 1) | (Bit(inner, 4) << 1) | (Bit(inner, 5) << 2) | (Bit(inner, 2) << 3) |
                 ((Bit(inner, 3) ^ Bit(inner, 9)) << 4);
            ly = Bit(inner, 0) | (Bit(inner, 6) << 1) | (Bit(inner, 8) << 2) | (Bit(inner, 9) << 3) |
                 (Bit(inner, 7) << 4);
        }

        return (lx, ly);
    }

    /// <summary>Inverse of <see cref="InnerToLxy"/>: encodes local (lx, ly) block coordinates back to the 10-bit Morton "inner" index.</summary>
    public static int LxyToInner(int lx, int ly, int logBpb)
    {
        int Bit(int v, int n) => (v >> n) & 1;

        if (logBpb == 3)
        {
            return Bit(lx, 0) | (Bit(ly, 0) << 1) | (Bit(lx, 1) << 2) | (Bit(lx, 3) << 3) |
                   ((Bit(lx, 4) ^ Bit(ly, 3)) << 4) | (Bit(lx, 2) << 5) | (Bit(ly, 1) << 6) |
                   (Bit(ly, 2) << 7) | (Bit(ly, 4) << 8) | (Bit(ly, 3) << 9);
        }

        return Bit(ly, 0) | (Bit(lx, 0) << 1) | (Bit(lx, 3) << 2) | ((Bit(lx, 4) ^ Bit(ly, 3)) << 3) |
               (Bit(lx, 1) << 4) | (Bit(lx, 2) << 5) | (Bit(ly, 1) << 6) | (Bit(ly, 4) << 7) |
               (Bit(ly, 2) << 8) | (Bit(ly, 3) << 9);
    }

    private static int LogBpb(int bpb) => (int)BitOperations.Log2((uint)bpb);

    private static int AlignUp32(int value) => (value + 31) & ~31;

    /// <summary>Byte size of a single mip level's data once tiled (32-block aligned in both dimensions).</summary>
    public static int TiledMipSize(int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        return TiledElementsSize(blockWidth, blockHeight, bpb);
    }

    /// <summary>Converts tiled+endian-swapped DXT block data to linear (row-major) block order, ready for BC decompression.</summary>
    public static byte[] Untile(byte[] tiledData, int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        return UntileElements(tiledData, blockWidth, blockHeight, bpb);
    }

    /// <summary>Converts linear (row-major) DXT block data to X360 tiled order (32-block aligned, padded with zeros).</summary>
    public static byte[] Tile(byte[] linearData, int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        return TileElements(linearData, blockWidth, blockHeight, bpb);
    }

    /// <summary>
    /// EXPERIMENTAL. Raw (non-block-compressed) X360 pixel formats -- A8R8G8B8 (0x86),
    /// A1R5G5B5 (0x43), B5G6R5 (0x44) -- are tiled by a scheme the reference tooling
    /// delegates to a native, closed-source routine (Noesis's <c>imageUntile360Raw</c>);
    /// no Python/algorithmic source for it was available to port. This treats each pixel
    /// as its own "block" (bpb = bytes per pixel, no 4x4 grouping) and reuses the same
    /// Morton macro-tile math verified for DXT blocks -- a principled hypothesis, not a
    /// verified port. Revisit if raw-format textures are found to decode with visibly
    /// wrong pixels; DXT1/3/5/BC5 (the vast majority of jersey/gear textures) do not
    /// depend on this path at all.
    /// </summary>
    public static byte[] UntileRawPixelsExperimental(byte[] tiledData, int width, int height, int bytesPerPixel) =>
        UntileElements(tiledData, width, height, bytesPerPixel);

    /// <summary>EXPERIMENTAL inverse of <see cref="UntileRawPixelsExperimental"/> -- see its remarks.</summary>
    public static byte[] TileRawPixelsExperimental(byte[] linearData, int width, int height, int bytesPerPixel) =>
        TileElements(linearData, width, height, bytesPerPixel);

    /// <summary>Byte size of a single raw-pixel mip level once tiled (see <see cref="UntileRawPixelsExperimental"/>).</summary>
    public static int TiledRawPixelsSize(int width, int height, int bytesPerPixel) =>
        TiledElementsSize(width, height, bytesPerPixel);

    /// <summary>
    /// In-place variant of <see cref="Tile"/>: writes each real (non-padding) DXT block
    /// into <paramref name="destinationTiledBuffer"/> at its tiled position without
    /// touching any other byte. This is what makes the InPlace patch strategy reproduce
    /// the original file byte-for-byte when nothing actually changed -- whatever the
    /// original padding-region bytes were (zero or otherwise), they are preserved exactly
    /// instead of being reset to zero by a from-scratch <see cref="Tile"/> call.
    /// </summary>
    public static void TileInto(byte[] destinationTiledBuffer, int destinationOffset, byte[] linearData, int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        TileElementsInto(destinationTiledBuffer, destinationOffset, linearData, blockWidth, blockHeight, bpb);
    }

    /// <summary>EXPERIMENTAL in-place variant of <see cref="UntileRawPixelsExperimental"/>'s inverse -- see its remarks.</summary>
    public static void TileRawPixelsIntoExperimental(byte[] destinationTiledBuffer, int destinationOffset, byte[] linearData, int width, int height, int bytesPerPixel) =>
        TileElementsInto(destinationTiledBuffer, destinationOffset, linearData, width, height, bytesPerPixel);

    private static void TileElementsInto(byte[] destinationTiledBuffer, int destinationOffset, byte[] linearData, int elementsWide, int elementsHigh, int bpb)
    {
        int tiledWide = AlignUp32(elementsWide);
        int tilesPerRow = tiledWide / 32;
        int logBpb = LogBpb(bpb);

        for (int ey = 0; ey < elementsHigh; ey++)
        {
            for (int ex = 0; ex < elementsWide; ex++)
            {
                int macroCol = ex / 32;
                int macroRow = ey / 32;
                int macroIndex = macroCol + macroRow * tilesPerRow;
                int inner = LxyToInner(ex % 32, ey % 32, logBpb);
                int bi = (macroIndex << 10) | inner;
                int src = (ey * elementsWide + ex) * bpb;
                int dst = destinationOffset + (bi * bpb);

                if (dst + bpb <= destinationTiledBuffer.Length && dst >= destinationOffset && src + bpb <= linearData.Length)
                {
                    Array.Copy(linearData, src, destinationTiledBuffer, dst, bpb);
                }
            }
        }
    }

    private static int TiledElementsSize(int elementsWide, int elementsHigh, int bpb)
    {
        int tiledWide = AlignUp32(elementsWide);
        int tiledHigh = AlignUp32(elementsHigh);
        return tiledWide * tiledHigh * bpb;
    }

    private static byte[] UntileElements(byte[] tiledData, int elementsWide, int elementsHigh, int bpb)
    {
        int tiledWide = AlignUp32(elementsWide);
        int tiledHigh = AlignUp32(elementsHigh);
        int tilesPerRow = tiledWide / 32;
        int logBpb = LogBpb(bpb);

        var result = new byte[elementsWide * elementsHigh * bpb];
        int totalElements = tiledWide * tiledHigh;

        for (int bi = 0; bi < totalElements; bi++)
        {
            int macroIndex = bi >> 10;
            int inner = bi & 1023;
            int macroCol = macroIndex % tilesPerRow;
            int macroRow = macroIndex / tilesPerRow;
            (int lx, int ly) = InnerToLxy(inner, logBpb);
            int ex = macroCol * 32 + lx;
            int ey = macroRow * 32 + ly;
            int src = bi * bpb;

            if (ex < elementsWide && ey < elementsHigh && src + bpb <= tiledData.Length)
            {
                int dst = (ey * elementsWide + ex) * bpb;
                Array.Copy(tiledData, src, result, dst, bpb);
            }
        }

        return result;
    }

    private static byte[] TileElements(byte[] linearData, int elementsWide, int elementsHigh, int bpb)
    {
        int tiledWide = AlignUp32(elementsWide);
        int tiledHigh = AlignUp32(elementsHigh);
        int tilesPerRow = tiledWide / 32;
        int logBpb = LogBpb(bpb);

        var result = new byte[tiledWide * tiledHigh * bpb];

        for (int ey = 0; ey < elementsHigh; ey++)
        {
            for (int ex = 0; ex < elementsWide; ex++)
            {
                int macroCol = ex / 32;
                int macroRow = ey / 32;
                int macroIndex = macroCol + macroRow * tilesPerRow;
                int inner = LxyToInner(ex % 32, ey % 32, logBpb);
                int bi = (macroIndex << 10) | inner;
                int src = (ey * elementsWide + ex) * bpb;
                int dst = bi * bpb;
                Array.Copy(linearData, src, result, dst, bpb);
            }
        }

        return result;
    }

    /// <summary>
    /// The canonical <c>XGAddress2DTiledOffset</c> X coordinate from the Xbox 360 XDK --
    /// the tiling every known-working 360 tool uses for RAW (non-block-compressed) pixel
    /// formats. Verified against a real game texture: the XPR2-wrapped 128x128 A8R8G8B8
    /// team logo in <c>d24.big</c> decodes pixel-perfect through this (and visibly wrong
    /// through the Morton-macro-tile scheme <see cref="UntileRawPixelsExperimental"/>
    /// guessed at -- that path is kept only because the RX2 reader already ships on it and
    /// changing RX2 behavior is out of scope here).
    /// </summary>
    public static int XdkTiledX(int offset, int width, int texelPitch)
    {
        int alignedWidth = (width + 31) & ~31;
        int logBpp = (texelPitch >> 2) + ((texelPitch >> 1) >> (texelPitch >> 2));
        int offsetB = offset << logBpp;
        int offsetT = ((offsetB & ~4095) >> 3) + ((offsetB & 1792) >> 2) + (offsetB & 63);
        int offsetM = offsetT >> (7 + logBpp);

        int macroX = (offsetM % (alignedWidth >> 5)) << 2;
        int tile = ((((offsetT >> (5 + logBpp)) & 2) + (offsetB >> 6)) & 3);
        int macro = (macroX + tile) << 3;
        int micro = ((((offsetT >> 1) & ~15) + (offsetT & 15)) & ((texelPitch << 3) - 1)) >> logBpp;

        return macro + micro;
    }

    /// <summary>The canonical <c>XGAddress2DTiledOffset</c> Y coordinate -- see <see cref="XdkTiledX"/>.</summary>
    public static int XdkTiledY(int offset, int width, int texelPitch)
    {
        int alignedWidth = (width + 31) & ~31;
        int logBpp = (texelPitch >> 2) + ((texelPitch >> 1) >> (texelPitch >> 2));
        int offsetB = offset << logBpp;
        int offsetT = ((offsetB & ~4095) >> 3) + ((offsetB & 1792) >> 2) + (offsetB & 63);
        int offsetM = offsetT >> (7 + logBpp);

        int macroY = (offsetM / (alignedWidth >> 5)) << 2;
        int tile = ((offsetT >> (6 + logBpp)) & 1) + ((offsetB & 2048) >> 10);
        int macro = (macroY + tile) << 3;
        int micro = ((((offsetT & (((texelPitch << 6) - 1) & ~31)) + ((offsetT & 15) << 1)) >> (3 + logBpp)) & ~1);

        return macro + micro + ((offsetT & 16) >> 4);
    }

    /// <summary>Converts XDK-tiled raw pixel data to linear row-major order -- see <see cref="XdkTiledX"/> for provenance.</summary>
    public static byte[] XdkUntileRawPixels(byte[] tiledData, int width, int height, int bytesPerPixel)
    {
        var result = new byte[width * height * bytesPerPixel];
        int texelCount = width * height;

        for (int offset = 0; offset < texelCount; offset++)
        {
            int x = XdkTiledX(offset, width, bytesPerPixel);
            int y = XdkTiledY(offset, width, bytesPerPixel);
            int src = offset * bytesPerPixel;

            if (x < width && y < height && src + bytesPerPixel <= tiledData.Length)
            {
                Array.Copy(tiledData, src, result, (y * width + x) * bytesPerPixel, bytesPerPixel);
            }
        }

        return result;
    }

    /// <summary>Inverse of <see cref="XdkUntileRawPixels"/>: converts linear row-major raw pixels to XDK tiled order.</summary>
    public static byte[] XdkTileRawPixels(byte[] linearData, int width, int height, int bytesPerPixel)
    {
        var result = new byte[width * height * bytesPerPixel];
        int texelCount = width * height;

        for (int offset = 0; offset < texelCount; offset++)
        {
            int x = XdkTiledX(offset, width, bytesPerPixel);
            int y = XdkTiledY(offset, width, bytesPerPixel);
            int src = (y * width + x) * bytesPerPixel;

            if (x < width && y < height && src + bytesPerPixel <= linearData.Length)
            {
                Array.Copy(linearData, src, result, offset * bytesPerPixel, bytesPerPixel);
            }
        }

        return result;
    }

    /// <summary>Byte-swaps every 16-bit word in place semantics (returns a new array). X360 stores tiled DXT/raw data as big-endian 16-bit words.</summary>
    public static byte[] SwapEndian16(byte[] data)
    {
        var result = new byte[data.Length];
        int pairCount = data.Length / 2;
        for (int i = 0; i < pairCount; i++)
        {
            result[2 * i] = data[(2 * i) + 1];
            result[(2 * i) + 1] = data[2 * i];
        }

        if ((data.Length & 1) != 0)
        {
            result[^1] = data[^1];
        }

        return result;
    }
}
