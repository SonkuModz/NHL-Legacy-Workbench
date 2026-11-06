using System.Buffers.Binary;
using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Replaces one submesh's vertex/index buffers in an RX2 file, following the exact
/// non-destructive discipline <see cref="Rx2Writer.Rebuild"/> uses for textures: clone the
/// whole file, touch only the specific buffers being replaced (overwrite in place if the new
/// data fits the original slot, append past the current end of the file if it doesn't), and
/// patch only that buffer's own file-table entry. This matters here for the same reason it
/// mattered for the stadium-texture bug -- combined mesh+texture containers share their
/// GPU-data region between texture pixel data and mesh vertex/index buffers, so a writer that
/// repacks or otherwise touches bytes outside the specific buffer(s) being replaced risks
/// corrupting unrelated textures or submeshes.
///
/// Two hard-won correctness rules, both root-caused from a real in-game corruption (giant
/// exploded white stadium geometry after a powerring edit):
///
/// 1. Stadium vertices carry far more than position + UV1 -- packed normals, vertex colors,
///    and up to four lightmap UV channels, none of which the app's mesh pipeline models.
///    Zero-filling them turns the whole arena flat white. So every new vertex's byte layout
///    is seeded from an original vertex (exact position match first, nearest position
///    otherwise) before position/UV1 are overwritten on top.
/// 2. The FInfo/VInfo descriptor blocks carry their own byte-size and vertex-count fields
///    (see <see cref="Rx2ModelLayout"/>). The game renders with THOSE, not just the
///    file-table entry sizes -- leaving them stale after a count-changing import makes the
///    GPU read past the real vertices into garbage. They are patched on every write.
/// </summary>
public static class Rx2ModelWriter
{
    /// <summary>RX2 index buffers are 16-bit, so a single submesh can never exceed this many vertices.</summary>
    public const int MaxVertexCount = 65535;

    /// <param name="attributeSourceIndices">Optional explicit mapping: for each new vertex, the
    /// index of the original vertex whose unmodeled attributes (normals, colors, lightmap UVs)
    /// it inherits. Used by in-place transforms (e.g. the Model Viewer's scale tool), where the
    /// vertex order is unchanged and position-based matching would mis-pair vertices that moved.
    /// When null, mapping falls back to exact-position then nearest-position matching.</param>
    public static Result<byte[]> ReplaceSubmesh(
        byte[] originalData,
        Rx2MeshWriteBackInfo info,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> uvs,
        IReadOnlyList<int> triangleIndices,
        IReadOnlyList<int>? attributeSourceIndices = null)
    {
        if (positions.Count == 0)
        {
            return Result.Failure<byte[]>("Cannot write a submesh with zero vertices.");
        }

        if (positions.Count > MaxVertexCount)
        {
            return Result.Failure<byte[]>($"Cannot write {positions.Count} vertices -- RX2 index buffers are 16-bit, so a single submesh is limited to {MaxVertexCount} vertices.");
        }

        if (uvs.Count != positions.Count)
        {
            return Result.Failure<byte[]>("Vertex and UV counts must match.");
        }

        if (triangleIndices.Count % 3 != 0)
        {
            return Result.Failure<byte[]>("Triangle index count must be a multiple of 3.");
        }

        foreach (int index in triangleIndices)
        {
            if (index < 0 || index >= positions.Count)
            {
                return Result.Failure<byte[]>($"Triangle index {index} is out of range for {positions.Count} vertices.");
            }
        }

        if (attributeSourceIndices is not null && attributeSourceIndices.Count != positions.Count)
        {
            return Result.Failure<byte[]>("attributeSourceIndices must have one entry per vertex.");
        }

        byte[] newVertexBuffer = BuildVertexBuffer(originalData, info, positions, uvs, attributeSourceIndices);

        byte[] newIndexBuffer = new byte[triangleIndices.Count * 2];
        for (int i = 0; i < triangleIndices.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(newIndexBuffer.AsSpan(i * 2, 2), (ushort)triangleIndices[i]);
        }

        byte[] buffer = (byte[])originalData.Clone();
        int originalLength = buffer.Length;

        if (!TryPatchBuffer(ref buffer, info.VertexBufferEntryIndex, newVertexBuffer, info.HeaderSize, Rx2ModelLayout.VertexBufferSlotAlignment, out int vertexEntrySize, out string? error))
        {
            return Result.Failure<byte[]>(error!);
        }

        if (!TryPatchBuffer(ref buffer, info.FaceBufferEntryIndex, newIndexBuffer, info.HeaderSize, Rx2ModelLayout.IndexBufferSlotAlignment, out int faceEntrySize, out error))
        {
            return Result.Failure<byte[]>(error!);
        }

        // The renderer draws with the FInfo/VInfo counts and sizes, not the file-table entry
        // fields -- every one of these must track the new buffers or the game reads garbage.
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.IndexCountFieldAbsoluteOffset, 4), triangleIndices.Count);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.IndexBufferSizeFieldAbsoluteOffset, 4), faceEntrySize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(info.VertexBufferSizeFieldAbsoluteOffset, 4), Rx2ModelLayout.VertexInfoSizeFlag | (uint)(vertexEntrySize + 2));
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.VertexCountFieldAbsoluteOffset, 4), positions.Count);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(info.PrimitiveTypeFieldAbsoluteOffset, 4), Rx2ModelLayout.PrimitiveTypeTriangleList);

        if (buffer.Length > originalLength)
        {
            uint newTotalGpuSize = (uint)(buffer.Length - info.HeaderSize);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(Rx2HeaderLayout.TotalGpuDataSizeOffset, 4), newTotalGpuSize);
        }

        return Result.Success(buffer);
    }

    /// <summary>
    /// Encodes the new vertex buffer. Each vertex starts as a byte-for-byte copy of an
    /// original vertex (chosen by exact position match, falling back to nearest position) so
    /// that every attribute this pipeline doesn't model -- packed normals, vertex colors,
    /// lightmap UV channels -- survives the round trip; position and UV1 are then written
    /// over the copy. Falls back to a zero template only if the original buffer can't be
    /// located (which never happens for write-back infos produced by <see cref="Rx2ModelReader"/>).
    /// </summary>
    private static byte[] BuildVertexBuffer(byte[] originalData, Rx2MeshWriteBackInfo info, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector2> uvs, IReadOnlyList<int>? attributeSourceIndices)
    {
        byte[] newVertexBuffer = new byte[positions.Count * info.Stride];

        byte[]? originalVertexBuffer = TryReadOriginalVertexBuffer(originalData, info, out int originalVertexCount);
        int[]? sourceMap = originalVertexBuffer is null
            ? null
            : attributeSourceIndices is not null && attributeSourceIndices.All(i => i >= 0 && i < originalVertexCount)
                ? attributeSourceIndices.ToArray()
                : MapToOriginalVertices(positions, originalVertexBuffer, originalVertexCount, info);

        for (int v = 0; v < positions.Count; v++)
        {
            int baseOffset = v * info.Stride;
            if (sourceMap is not null)
            {
                Array.Copy(originalVertexBuffer!, sourceMap[v] * info.Stride, newVertexBuffer, baseOffset, info.Stride);
            }

            if (info.PositionOffset.HasValue)
            {
                WritePosition(newVertexBuffer, baseOffset + info.PositionOffset.Value, info.PositionType, positions[v]);
            }

            if (info.UvOffset.HasValue)
            {
                WriteUv(newVertexBuffer, baseOffset + info.UvOffset.Value, info.UvType, uvs[v]);
            }
        }

        return newVertexBuffer;
    }

    private static byte[]? TryReadOriginalVertexBuffer(byte[] originalData, Rx2MeshWriteBackInfo info, out int originalVertexCount)
    {
        originalVertexCount = 0;
        if (info.Stride <= 0 || info.OriginalVertexCount <= 0)
        {
            return null;
        }

        uint fileCount = BigEndianReader.ReadUInt32BEAt(originalData, Rx2HeaderLayout.FileCountOffset);
        uint fileTableOffset = BigEndianReader.ReadUInt32BEAt(originalData, Rx2HeaderLayout.FileTableOffset);
        if (info.VertexBufferEntryIndex < 0 || info.VertexBufferEntryIndex >= fileCount)
        {
            return null;
        }

        int entryTableOffset = (int)fileTableOffset + (info.VertexBufferEntryIndex * Rx2HeaderLayout.FileTableEntrySize);
        int offsetField = BigEndianReader.ReadInt32BEAt(originalData, entryTableOffset + Rx2HeaderLayout.EntryOffsetField);
        int sizeField = BigEndianReader.ReadInt32BEAt(originalData, entryTableOffset + Rx2HeaderLayout.EntrySizeField);

        byte[] slice = BufferUtilities.SafeSlice(originalData, offsetField + info.HeaderSize, sizeField);
        originalVertexCount = Math.Min(info.OriginalVertexCount, slice.Length / info.Stride);
        return originalVertexCount > 0 ? slice : null;
    }

    /// <summary>
    /// For each new vertex, picks the original vertex whose attributes it should inherit:
    /// an exact position match when the vertex wasn't moved (the common case for re-imports,
    /// since position decode/encode is byte-lossless), otherwise the nearest original
    /// position via a uniform grid (moved or newly created vertices get the attributes of
    /// their closest surviving neighbor -- the best available guess for paint-style data
    /// like vertex colors and lightmap UVs).
    /// </summary>
    private static int[] MapToOriginalVertices(IReadOnlyList<Vector3> positions, byte[] originalVertexBuffer, int originalVertexCount, Rx2MeshWriteBackInfo info)
    {
        var originalPositions = new Vector3[originalVertexCount];
        var exact = new Dictionary<Vector3, int>(originalVertexCount);
        for (int i = 0; i < originalVertexCount; i++)
        {
            originalPositions[i] = info.PositionOffset.HasValue
                ? ReadPosition(originalVertexBuffer, (i * info.Stride) + info.PositionOffset.Value, info.PositionType)
                : Vector3.Zero;
            exact.TryAdd(originalPositions[i], i);
        }

        var grid = new PositionGrid(originalPositions);
        var map = new int[positions.Count];
        for (int v = 0; v < positions.Count; v++)
        {
            // Same slot, same position -- an unmoved vertex in an order-preserving re-import.
            // Preferring it over the exact-match dictionary keeps each vertex's OWN
            // attributes when several vertices share a position (seam vertices with
            // different colors/normals would otherwise all inherit the first duplicate's).
            if (v < originalVertexCount && positions[v] == originalPositions[v])
            {
                map[v] = v;
            }
            else
            {
                map[v] = exact.TryGetValue(positions[v], out int match) ? match : grid.FindNearest(positions[v]);
            }
        }

        return map;
    }

    /// <summary>
    /// Overwrites the given file-table entry's buffer in place if <paramref name="newData"/>
    /// fits in its existing slot (zeroing any slack and leaving the Offset/Size fields
    /// untouched, so the slot stays available for future edits), or appends past the current
    /// end of <paramref name="buffer"/> -- at a 16-byte-aligned offset, padded out to the
    /// slot alignment real files use -- and repoints the entry's Offset/Size fields.
    /// Re-reads the file table fresh from <paramref name="buffer"/> on every call (rather
    /// than caching it) so this stays correct across the second call after the first one may
    /// have grown the buffer. <paramref name="entrySize"/> returns the slot size the
    /// descriptor blocks must advertise (the preserved original size in place, the padded
    /// size on append).
    /// </summary>
    private static bool TryPatchBuffer(ref byte[] buffer, int entryIndex, byte[] newData, int headerSize, int slotAlignment, out int entrySize, out string? error)
    {
        error = null;
        entrySize = 0;
        uint fileCount = BigEndianReader.ReadUInt32BEAt(buffer, Rx2HeaderLayout.FileCountOffset);
        uint fileTableOffset = BigEndianReader.ReadUInt32BEAt(buffer, Rx2HeaderLayout.FileTableOffset);

        if (entryIndex < 0 || entryIndex >= fileCount)
        {
            error = $"Buffer entry index {entryIndex} is out of range for a file table of {fileCount} entries.";
            return false;
        }

        int entryTableOffset = (int)fileTableOffset + (entryIndex * Rx2HeaderLayout.FileTableEntrySize);
        int originalOffsetField = BigEndianReader.ReadInt32BEAt(buffer, entryTableOffset + Rx2HeaderLayout.EntryOffsetField);
        int originalSizeField = BigEndianReader.ReadInt32BEAt(buffer, entryTableOffset + Rx2HeaderLayout.EntrySizeField);

        if (newData.Length <= originalSizeField)
        {
            // Fits inside the buffer's existing slot -- overwrite in place and keep the
            // Offset/Size fields exactly as they were. Any slack past the new data is
            // unreferenced (the descriptor counts written by the caller bound what the game
            // reads), so its original bytes are deliberately left in place rather than
            // zeroed -- byte preservation over cosmetic cleanup.
            Array.Copy(newData, 0, buffer, originalOffsetField + headerSize, newData.Length);
            entrySize = originalSizeField;
            return true;
        }

        // Doesn't fit -- append past the current end of the file so nothing already written
        // (textures, other submeshes' buffers) ever has to move. Real files keep every GPU
        // buffer at least 16-byte aligned and size vertex/index slots to 32/16-byte multiples.
        int paddedSize = AlignUp(newData.Length, slotAlignment);
        int newOffsetAbsolute = AlignUp(buffer.Length, Rx2ModelLayout.GpuBufferOffsetAlignment);
        Array.Resize(ref buffer, newOffsetAbsolute + paddedSize);
        Array.Copy(newData, 0, buffer, newOffsetAbsolute, newData.Length);

        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(entryTableOffset + Rx2HeaderLayout.EntryOffsetField, 4), newOffsetAbsolute - headerSize);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(entryTableOffset + Rx2HeaderLayout.EntrySizeField, 4), paddedSize);
        entrySize = paddedSize;
        return true;
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static Vector3 ReadPosition(byte[] data, int offset, int declType)
    {
        int requiredBytes = declType == Rx2ModelLayout.TypeFloat32 ? 12 : 6;
        if (offset < 0 || offset + requiredBytes > data.Length)
        {
            return Vector3.Zero;
        }

        if (declType == Rx2ModelLayout.TypeFloat32)
        {
            return new Vector3(
                BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4)),
                BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset + 4, 4)),
                BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset + 8, 4)));
        }

        return Rx2HalfFloat.DecodeVector3(data, offset);
    }

    private static void WritePosition(byte[] data, int offset, int declType, Vector3 value)
    {
        if (declType == Rx2ModelLayout.TypeFloat32)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value.X);
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset + 4, 4), value.Y);
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset + 8, 4), value.Z);
        }
        else
        {
            Rx2HalfFloat.EncodeVector3(data, offset, value);
        }
    }

    private static void WriteUv(byte[] data, int offset, int declType, Vector2 value)
    {
        switch (declType)
        {
            case Rx2ModelLayout.TypeUvFloat:
                BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value.X);
                BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset + 4, 4), value.Y);
                break;

            case Rx2ModelLayout.TypeUvHalfFloat:
                (data[offset], data[offset + 1]) = Rx2HalfFloat.Encode(value.X);
                (data[offset + 2], data[offset + 3]) = Rx2HalfFloat.Encode(value.Y);
                break;

            case Rx2ModelLayout.TypeUvUShort:
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), (ushort)Math.Clamp(value.X * 65535f, 0f, 65535f));
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2, 2), (ushort)Math.Clamp(value.Y * 65535f, 0f, 65535f));
                break;

            case Rx2ModelLayout.TypeUvShort:
                BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset, 2), (short)Math.Clamp((value.X * 65535f) - 32768f, short.MinValue, short.MaxValue));
                BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset + 2, 2), (short)Math.Clamp((value.Y * 65535f) - 32768f, short.MinValue, short.MaxValue));
                break;

            default:
                // Unrecognized type -- leave the template bytes (or zeros) untouched, matching
                // Rx2ModelReader.ReadUv's own Vector2.Zero fallback for the same case.
                break;
        }
    }

    /// <summary>
    /// Uniform-grid nearest-neighbor lookup over the original vertex positions. Cheap to
    /// build (one pass), and each query only scans outward ring by ring until it finds a
    /// candidate, so mapping even a 65k-vertex import stays far from the quadratic brute
    /// force that a plain nearest scan would cost.
    /// </summary>
    private sealed class PositionGrid
    {
        private const int CellsPerAxis = 32;

        private readonly Vector3[] _positions;
        private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = new();
        private readonly Vector3 _min;
        private readonly float _cellSize;

        public PositionGrid(Vector3[] positions)
        {
            _positions = positions;
            Vector3 min = positions[0];
            Vector3 max = positions[0];
            foreach (Vector3 p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            _min = min;
            float extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), max.Z - min.Z);
            _cellSize = extent > 0f ? extent / CellsPerAxis : 1f;

            for (int i = 0; i < positions.Length; i++)
            {
                (int, int, int) key = CellOf(positions[i]);
                if (!_cells.TryGetValue(key, out List<int>? list))
                {
                    _cells[key] = list = new List<int>();
                }

                list.Add(i);
            }
        }

        public int FindNearest(Vector3 query)
        {
            (int qx, int qy, int qz) = CellOf(query);
            int best = -1;
            float bestDistSq = float.MaxValue;
            int foundAtRadius = -1;

            for (int radius = 0; radius <= CellsPerAxis + 1; radius++)
            {
                for (int x = qx - radius; x <= qx + radius; x++)
                {
                    for (int y = qy - radius; y <= qy + radius; y++)
                    {
                        for (int z = qz - radius; z <= qz + radius; z++)
                        {
                            // Only the shell of the current radius -- inner cells were already scanned.
                            if (Math.Max(Math.Abs(x - qx), Math.Max(Math.Abs(y - qy), Math.Abs(z - qz))) != radius)
                            {
                                continue;
                            }

                            if (!_cells.TryGetValue((x, y, z), out List<int>? list))
                            {
                                continue;
                            }

                            foreach (int i in list)
                            {
                                float d = Vector3.DistanceSquared(_positions[i], query);
                                if (d < bestDistSq)
                                {
                                    bestDistSq = d;
                                    best = i;
                                }
                            }
                        }
                    }
                }

                // A hit found at radius r can still be beaten by a point one shell further
                // out, but no further -- one extra ring settles it.
                if (best >= 0 && foundAtRadius < 0)
                {
                    foundAtRadius = radius;
                }

                if (foundAtRadius >= 0 && radius > foundAtRadius)
                {
                    return best;
                }
            }

            if (best >= 0)
            {
                return best;
            }

            // Query far outside the original bounds -- brute-force the small remainder.
            for (int i = 0; i < _positions.Length; i++)
            {
                float d = Vector3.DistanceSquared(_positions[i], query);
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = i;
                }
            }

            return best;
        }

        private (int X, int Y, int Z) CellOf(Vector3 p) => (
            (int)MathF.Floor((p.X - _min.X) / _cellSize),
            (int)MathF.Floor((p.Y - _min.Y) / _cellSize),
            (int)MathF.Floor((p.Z - _min.Z) / _cellSize));
    }
}
