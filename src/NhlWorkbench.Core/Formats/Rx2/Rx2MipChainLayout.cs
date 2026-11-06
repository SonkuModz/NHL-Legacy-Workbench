namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Computes, per mip level, both the tiled (on-disk, 32-block-aligned) byte layout and
/// the linear (decoded, format-neutral) byte layout for a texture's whole mip chain.
/// Shared by <see cref="Rx2Reader"/> (to know where to slice each mip out of the raw GPU
/// blob) and <see cref="Rx2Writer"/> (to know where to patch each mip back in) so the two
/// can never drift out of sync with each other.
/// </summary>
internal readonly struct Rx2MipChainLayout
{
    public int[] TiledOffsets { get; }

    public int[] TiledSizes { get; }

    public int[] LinearOffsets { get; }

    public int[] LinearSizes { get; }

    public int TotalTiledSize { get; }

    public int TotalLinearSize { get; }

    private Rx2MipChainLayout(int[] tiledOffsets, int[] tiledSizes, int[] linearOffsets, int[] linearSizes, int totalTiledSize, int totalLinearSize)
    {
        TiledOffsets = tiledOffsets;
        TiledSizes = tiledSizes;
        LinearOffsets = linearOffsets;
        LinearSizes = linearSizes;
        TotalTiledSize = totalTiledSize;
        TotalLinearSize = totalLinearSize;
    }

    public static Rx2MipChainLayout Compute(int width, int height, int mipCount, FormatElementInfo formatInfo)
    {
        mipCount = Math.Max(1, mipCount);
        var tiledOffsets = new int[mipCount];
        var tiledSizes = new int[mipCount];
        var linearOffsets = new int[mipCount];
        var linearSizes = new int[mipCount];

        int tiledCursor = 0;
        int linearCursor = 0;

        for (int m = 0; m < mipCount; m++)
        {
            int mipWidth = Math.Max(1, width >> m);
            int mipHeight = Math.Max(1, height >> m);

            int tiledSize;
            int linearSize;

            if (formatInfo.Layout == PixelLayout.CompressedBlock)
            {
                int blocksWide = Math.Max(1, (mipWidth + 3) / 4);
                int blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
                tiledSize = X360Tiling.TiledMipSize(mipWidth, mipHeight, formatInfo.BytesPerElement);
                linearSize = blocksWide * blocksHigh * formatInfo.BytesPerElement;
            }
            else
            {
                tiledSize = X360Tiling.TiledRawPixelsSize(mipWidth, mipHeight, formatInfo.BytesPerElement);
                linearSize = mipWidth * mipHeight * formatInfo.BytesPerElement;
            }

            tiledOffsets[m] = tiledCursor;
            tiledSizes[m] = tiledSize;
            linearOffsets[m] = linearCursor;
            linearSizes[m] = linearSize;

            tiledCursor += tiledSize;
            linearCursor += linearSize;
        }

        return new Rx2MipChainLayout(tiledOffsets, tiledSizes, linearOffsets, linearSizes, tiledCursor, linearCursor);
    }
}
