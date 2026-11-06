using NhlWorkbench.Core.Formats.Rx2;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// The highest-value tests in this suite: <see cref="X360Tiling"/>'s bit-interleave
/// formulas are the highest-risk code in Core (a single swapped bit silently corrupts
/// every non-trivial texture without throwing). These tests check the formulas'
/// mathematical self-consistency independent of any real sample file.
/// </summary>
public class X360TilingTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void InnerLxyConversion_IsABijectionOverTheFull32x32MacroTile(int logBpb)
    {
        var seen = new HashSet<(int Lx, int Ly)>();

        for (int inner = 0; inner < 1024; inner++)
        {
            (int lx, int ly) = X360Tiling.InnerToLxy(inner, logBpb);

            Assert.InRange(lx, 0, 31);
            Assert.InRange(ly, 0, 31);
            Assert.True(seen.Add((lx, ly)), $"inner={inner} (logBpb={logBpb}) produced a (lx,ly) already seen -- the Morton mapping is not a bijection.");

            int roundTripped = X360Tiling.LxyToInner(lx, ly, logBpb);
            Assert.Equal(inner, roundTripped);
        }

        Assert.Equal(1024, seen.Count);
    }

    [Theory]
    [InlineData(4, 4, 8)]
    [InlineData(32, 32, 8)]
    [InlineData(64, 64, 8)]
    [InlineData(100, 100, 8)]
    [InlineData(33, 65, 8)]
    [InlineData(4, 4, 16)]
    [InlineData(32, 32, 16)]
    [InlineData(64, 64, 16)]
    [InlineData(100, 100, 16)]
    [InlineData(33, 65, 16)]
    public void TileThenUntile_RoundTripsBlockDataExactly(int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        var original = new byte[blockWidth * blockHeight * bpb];
        new Random(unchecked((width * 397) ^ (height * 31) ^ bpb)).NextBytes(original);

        byte[] tiled = X360Tiling.Tile(original, width, height, bpb);
        byte[] roundTripped = X360Tiling.Untile(tiled, width, height, bpb);

        Assert.Equal(original, roundTripped);
    }

    [Theory]
    [InlineData(4, 4, 8)]
    [InlineData(100, 100, 16)]
    public void TileInto_OnlyTouchesRealBlockPositions_LeavingPaddingUntouched(int width, int height, int bpb)
    {
        int blockWidth = Math.Max(1, (width + 3) / 4);
        int blockHeight = Math.Max(1, (height + 3) / 4);
        var linear = new byte[blockWidth * blockHeight * bpb];
        new Random(42).NextBytes(linear);

        // Start from a destination buffer full of a sentinel value that isn't produced by
        // Tile()'s zero-initialization, so we can detect whether TileInto disturbs padding.
        int tiledSize = X360Tiling.TiledMipSize(width, height, bpb);
        var destination = new byte[tiledSize];
        Array.Fill(destination, (byte)0xAB);

        X360Tiling.TileInto(destination, 0, linear, width, height, bpb);
        byte[] untiled = X360Tiling.Untile(destination, width, height, bpb);
        Assert.Equal(linear, untiled);

        int touchedByteCount = destination.Count(b => b != 0xAB);
        Assert.True(touchedByteCount <= linear.Length, "TileInto touched more bytes than the real (non-padding) block data occupies.");
    }
}
