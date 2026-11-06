using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// Decodes a <see cref="AptShapeRecord"/>'s real vector geometry -- closing the gap that made
/// every VectorShape in the preview a generic, unpositioned placeholder (2026-08-19, same day as
/// the placement/transform layer). Found by reading the external NCAA14 community research's own
/// "APT_GEOMETRY.md"/dated discovery entries on the "DAT" mesh format directly rather than
/// re-deriving blind: that research documents a dominant 256-byte "fixed six-vertex quad" leaf
/// record with magic words at leaf-relative <c>+0==0x100</c> (256, its own length),
/// <c>+56==2</c>, and <c>+60==0x40</c> (64), and six XY vertex pairs starting at leaf-relative
/// <c>+64</c> with a 32-byte stride.
///
/// Confirmed against real NHL bytes (not copied blind): NHL wraps that exact 256-byte leaf in a
/// 32-byte, single-child header (<c>[totalLength][childCount=1][0][16][childLength][1][0][16]</c>)
/// so every magic offset above is simply shifted by 32 -- <c>+32==0x100</c>, <c>+88==2</c>,
/// <c>+92==0x40</c>, vertices at <c>+96</c>. Verified on two independent real entries: entry '6'
/// (overlay_0, geometryKey=4) decodes to corners (0,0)-(2,2), an EXACT match to that same Shape's
/// independently-recorded declared size (2x2) in the movie's own character table -- two unrelated
/// byte regions agreeing is what makes this a confirmed fact, not a guess. The six vertices
/// consistently repeat two of the four rectangle corners (a standard "quad as two triangles"
/// encoding, corners v0/v1/v2 and v3/v4/v5), so taking the min/max X and min/max Y across all six
/// is a robust way to recover the rectangle regardless of exact winding order.
///
/// Only the single-child (<c>childCount == 1</c>) case is decoded. Real entries with more than one
/// child exist (larger records, e.g. 576/848/1120 bytes) but their nested structure wasn't
/// confirmed with the same rigor -- returning null for those (falling back to the existing
/// placeholder rendering) is preferred over guessing at an unverified layout.
/// </summary>
public static class AptShapeGeometry
{
    private const int WrapperHeaderSize = 32;
    private const int LeafSize = 256;
    private const int VertexStride = 32;
    private const int VertexCount = 6;

    public static (float X, float Y, float Width, float Height)? TryDecodeRectangle(byte[] data)
    {
        if (data.Length < WrapperHeaderSize + LeafSize)
        {
            return null;
        }

        uint totalLength = BigEndianReader.ReadUInt32BEAt(data, 0);
        uint childCount = BigEndianReader.ReadUInt32BEAt(data, 4);
        if (totalLength != (uint)data.Length || childCount != 1)
        {
            return null;
        }

        int leafStart = WrapperHeaderSize;
        uint leafSelfLength = BigEndianReader.ReadUInt32BEAt(data, leafStart);
        uint marker56 = BigEndianReader.ReadUInt32BEAt(data, leafStart + 56);
        uint marker60 = BigEndianReader.ReadUInt32BEAt(data, leafStart + 60);
        if (leafSelfLength != LeafSize || marker56 != 2 || marker60 != 0x40)
        {
            return null;
        }

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        int verticesStart = leafStart + 64;
        for (int v = 0; v < VertexCount; v++)
        {
            int offset = verticesStart + (v * VertexStride);
            float x = BigEndianReader.ReadSingleBEAt(data, offset);
            float y = BigEndianReader.ReadSingleBEAt(data, offset + 4);
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minY) || !float.IsFinite(maxY))
        {
            return null;
        }

        return (minX, minY, maxX - minX, maxY - minY);
    }
}
