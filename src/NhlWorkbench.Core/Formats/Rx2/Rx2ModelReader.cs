using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Parses RX2-as-mesh files (a completely different payload than RX2-as-texture-container,
/// despite sharing the same magic bytes and outer file-table structure). Ported directly
/// from the shipped Noesis Python model importer's read sequence -- see
/// <see cref="Rx2ModelLayout"/> for the offset/field documentation.
/// </summary>
public static class Rx2ModelReader
{
    public static Result<Rx2Model> Parse(byte[] data, string sourcePath)
    {
        try
        {
            if (!Rx2Reader.IsRx2(data))
            {
                return Result.Failure<Rx2Model>("Not an RX2 file (magic mismatch).");
            }

            var warnings = new List<ParseWarning>();

            if (data.Length < Rx2HeaderLayout.HeaderSizeOffset + 4)
            {
                return Result.Failure<Rx2Model>("File is too small to contain an RX2 header.");
            }

            uint fileCount = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileCountOffset);
            uint fileTableOffset = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileTableOffset);
            uint headerSize = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.HeaderSizeOffset);
            uint totalDataSize = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.TotalGpuDataSizeOffset);
            if (totalDataSize == 0)
            {
                // Matches the reference importer: a zero data-size field means the header-size
                // field isn't meaningful for this file, so buffer offsets are absolute, not
                // header-relative.
                headerSize = 0;
            }

            if (fileTableOffset >= data.Length)
            {
                return Result.Failure<Rx2Model>("RX2 file table offset is out of range.");
            }

            int entryBase = (int)fileTableOffset;
            long entryCount = Math.Min(fileCount, (data.Length - entryBase) / (long)Rx2HeaderLayout.FileTableEntrySize);
            var entries = new Rx2FileTableEntry[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = Rx2FileTableEntry.ReadAt(data, entryBase + (i * Rx2HeaderLayout.FileTableEntrySize));
            }

            var meshes = new List<Rx2Mesh>();
            int meshOrdinal = 0;

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].TypeTag != Rx2ModelLayout.TypeTagFxRenderableSimple)
                {
                    continue;
                }

                Rx2Mesh? mesh = TryParseSubmesh(data, entries, i, (int)headerSize, meshOrdinal, warnings);
                if (mesh is null)
                {
                    continue;
                }

                meshes.Add(mesh);
                meshOrdinal++;
            }

            return Result.Success(new Rx2Model { Meshes = meshes }, warnings);
        }
        catch (Exception ex)
        {
            return Result.Failure<Rx2Model>($"Unhandled error parsing RX2 model: {ex.Message}", ex);
        }
    }

    private static Rx2Mesh? TryParseSubmesh(byte[] data, Rx2FileTableEntry[] entries, int entryIndex, int headerSize, int meshOrdinal, List<ParseWarning> warnings)
    {
        try
        {
            int renderableBase = entries[entryIndex].OffsetField;
            int objectTypeMeshIndex = BigEndianReader.ReadInt32BEAt(data, renderableBase + Rx2ModelLayout.RenderableObjectTypeMeshIndexOffset);
            int vertexDescIndex = BigEndianReader.ReadInt32BEAt(data, renderableBase + Rx2ModelLayout.RenderableVertexBufferDescIndexOffset);
            int primitiveType = BigEndianReader.ReadInt32BEAt(data, renderableBase + Rx2ModelLayout.RenderablePrimitiveTypeOffset);
            string name = ReadSubmeshName(data, renderableBase + 0x0C, meshOrdinal);

            if (!IsValidIndex(objectTypeMeshIndex, entries.Length) || !IsValidIndex(vertexDescIndex, entries.Length))
            {
                warnings.Add(new ParseWarning($"Submesh at file-table entry {entryIndex}: object-type-mesh or vertex-descriptor index out of range; skipping.", "Rx2ModelReader"));
                return null;
            }

            int objectTypeMeshBase = entries[objectTypeMeshIndex].OffsetField;
            int indexBufferInfoIndex = BigEndianReader.ReadInt32BEAt(data, objectTypeMeshBase + Rx2ModelLayout.ObjectTypeMeshIndexBufferInfoIndexOffset);
            int vertexBufferInfoIndex = BigEndianReader.ReadInt32BEAt(data, objectTypeMeshBase + Rx2ModelLayout.ObjectTypeMeshVertexBufferInfoIndexOffset);

            if (!IsValidIndex(indexBufferInfoIndex, entries.Length) || !IsValidIndex(vertexBufferInfoIndex, entries.Length))
            {
                warnings.Add(new ParseWarning($"Submesh at file-table entry {entryIndex}: index/vertex buffer-info index out of range; skipping.", "Rx2ModelReader"));
                return null;
            }

            int indexBufferInfoBase = entries[indexBufferInfoIndex].OffsetField + Rx2ModelLayout.BufferInfoHeaderSkip;
            int faceBufferIndex = BigEndianReader.ReadInt32BEAt(data, indexBufferInfoBase + Rx2ModelLayout.FaceInfoBufferIndexOffset);
            int indexCount = BigEndianReader.ReadInt32BEAt(data, indexBufferInfoBase + Rx2ModelLayout.FaceInfoIndexCountOffset);

            int vertexBufferInfoBase = entries[vertexBufferInfoIndex].OffsetField + Rx2ModelLayout.BufferInfoHeaderSkip;
            int packedVertexBufferIndex = BigEndianReader.ReadInt32BEAt(data, vertexBufferInfoBase + Rx2ModelLayout.VertexInfoPackedBufferIndexOffset);
            int vertexBufferIndex = packedVertexBufferIndex >> 2;

            if (!IsValidIndex(faceBufferIndex, entries.Length) || !IsValidIndex(vertexBufferIndex, entries.Length))
            {
                warnings.Add(new ParseWarning($"Submesh at file-table entry {entryIndex}: face/vertex buffer index out of range; skipping.", "Rx2ModelReader"));
                return null;
            }

            // Vertex element declarations.
            int vertexDescBase = entries[vertexDescIndex].OffsetField;
            int elementCount = data[vertexDescBase + Rx2ModelLayout.VertexDescElementCountOffset];
            int stride = data[vertexDescBase + Rx2ModelLayout.VertexDescStrideOffset];

            int? positionOffset = null;
            int positionType = 0;
            int? uvOffset = null;
            int uvType = 0;

            for (int e = 0; e < elementCount; e++)
            {
                int elementBase = vertexDescBase + Rx2ModelLayout.VertexDescElementListOffset + (e * Rx2ModelLayout.VertexDescElementSize);
                int byteOffset = BigEndianReader.ReadInt32BEAt(data, elementBase);
                int declType = BigEndianReader.ReadInt32BEAt(data, elementBase + 4);
                int declUsage = BigEndianReader.ReadInt32BEAt(data, elementBase + 8);

                if (declUsage == Rx2ModelLayout.UsagePosition)
                {
                    positionOffset = byteOffset;
                    positionType = declType;
                }
                else if (declUsage == Rx2ModelLayout.UsageTexCoord0)
                {
                    uvOffset = byteOffset;
                    uvType = declType;
                }
            }

            if (positionOffset is null || stride <= 0)
            {
                warnings.Add(new ParseWarning($"Submesh at file-table entry {entryIndex}: no position element found; skipping.", "Rx2ModelReader"));
                return null;
            }

            byte[] vertexBuffer = BufferUtilities.SafeSlice(
                data, entries[vertexBufferIndex].OffsetField + headerSize, entries[vertexBufferIndex].SizeField);
            int derivedVertexCount = stride > 0 ? vertexBuffer.Length / stride : 0;

            // The VInfo count field is authoritative -- vertex buffers are 32-byte aligned, so
            // dividing the buffer size by the stride can over-count by a phantom padding vertex
            // (seen in real stadium files: a 3539-vertex mesh in a 3540-slot buffer).
            int declaredVertexCount = BigEndianReader.ReadInt32BEAt(data, vertexBufferInfoBase + Rx2ModelLayout.VertexInfoVertexCountOffset);
            int vertexCount = declaredVertexCount > 0 && declaredVertexCount <= derivedVertexCount
                ? declaredVertexCount
                : derivedVertexCount;

            var positions = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];

            for (int v = 0; v < vertexCount; v++)
            {
                int vertexBase = v * stride;
                positions[v] = ReadPosition(vertexBuffer, vertexBase + positionOffset.Value, positionType);
                uvs[v] = uvOffset.HasValue ? ReadUv(vertexBuffer, vertexBase + uvOffset.Value, uvType) : Vector2.Zero;
            }

            byte[] indexBuffer = BufferUtilities.SafeSlice(
                data, entries[faceBufferIndex].OffsetField + headerSize, entries[faceBufferIndex].SizeField);
            int actualIndexCount = Math.Min(indexCount, indexBuffer.Length / 2);
            var rawIndices = new ushort[actualIndexCount];
            for (int idx = 0; idx < actualIndexCount; idx++)
            {
                rawIndices[idx] = BigEndianReader.ReadUInt16BEAt(indexBuffer, idx * 2);
            }

            List<int> triangleIndices = primitiveType == Rx2ModelLayout.PrimitiveTypeTriangleStrip
                ? Rx2TriangleStrip.FromTriangleStrip(rawIndices)
                : Rx2TriangleStrip.FromTriangleList(rawIndices);

            // Drop any triangle referencing an out-of-range vertex rather than throwing --
            // a malformed/truncated buffer degrades to a partial mesh, not a crash.
            var safeTriangles = new List<int>(triangleIndices.Count);
            for (int t = 0; t + 2 < triangleIndices.Count; t += 3)
            {
                int a = triangleIndices[t];
                int b = triangleIndices[t + 1];
                int c = triangleIndices[t + 2];
                if (a < vertexCount && b < vertexCount && c < vertexCount)
                {
                    safeTriangles.Add(a);
                    safeTriangles.Add(b);
                    safeTriangles.Add(c);
                }
            }

            Vector3[] normals = Rx2MeshGeometry.ComputeSmoothNormals(positions, safeTriangles);
            Vector3[] tangents = Rx2MeshGeometry.ComputeTangents(positions, uvs, safeTriangles);

            var vertices = new Rx2Vertex[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                vertices[v] = new Rx2Vertex(positions[v], normals[v], tangents[v], uvs[v]);
            }

            var writeBackInfo = new Rx2MeshWriteBackInfo
            {
                VertexBufferEntryIndex = vertexBufferIndex,
                FaceBufferEntryIndex = faceBufferIndex,
                IndexCountFieldAbsoluteOffset = indexBufferInfoBase + Rx2ModelLayout.FaceInfoIndexCountOffset,
                IndexBufferSizeFieldAbsoluteOffset = indexBufferInfoBase + Rx2ModelLayout.FaceInfoBufferSizeOffset,
                VertexBufferSizeFieldAbsoluteOffset = vertexBufferInfoBase + Rx2ModelLayout.VertexInfoBufferSizeOffset,
                VertexCountFieldAbsoluteOffset = vertexBufferInfoBase + Rx2ModelLayout.VertexInfoVertexCountOffset,
                OriginalVertexCount = vertexCount,
                PrimitiveTypeFieldAbsoluteOffset = renderableBase + Rx2ModelLayout.RenderablePrimitiveTypeOffset,
                Stride = stride,
                PositionOffset = positionOffset,
                PositionType = positionType,
                UvOffset = uvOffset,
                UvType = uvType,
                HeaderSize = headerSize,
            };

            return new Rx2Mesh
            {
                Name = name,
                Vertices = vertices,
                TriangleIndices = safeTriangles.ToArray(),
                WriteBackInfo = writeBackInfo,
            };
        }
        catch (Exception ex)
        {
            warnings.Add(new ParseWarning($"Submesh at file-table entry {entryIndex}: {ex.Message}; skipping.", "Rx2ModelReader"));
            return null;
        }
    }

    /// <summary>
    /// Reads a short, bounded null-terminated ASCII name. Deliberately capped (unlike
    /// <see cref="BigEndianReader.ReadNullTerminatedAsciiAt"/>, which scans unbounded) --
    /// this offset isn't format-guaranteed to always hold a real name, and a mesh name is
    /// cosmetic, so a bounded best-effort read is safer than risking a multi-megabyte scan.
    /// </summary>
    internal static string ReadSubmeshName(byte[] data, int offset, int meshOrdinal)
    {
        const int MaxNameLength = 64;
        if (offset < 0 || offset >= data.Length)
        {
            return $"submesh_{meshOrdinal}";
        }

        int end = Math.Min(data.Length, offset + MaxNameLength);
        int pos = offset;
        while (pos < end && data[pos] != 0)
        {
            pos++;
        }

        if (pos == end || pos == offset)
        {
            return $"submesh_{meshOrdinal}";
        }

        string name = System.Text.Encoding.ASCII.GetString(data, offset, pos - offset);
        bool isPrintable = name.All(c => c is >= (char)0x20 and <= (char)0x7E);
        return isPrintable ? name : $"submesh_{meshOrdinal}";
    }

    private static bool IsValidIndex(int index, int length) => index >= 0 && index < length;

    private static Vector3 ReadPosition(byte[] data, int offset, int declType)
    {
        int requiredBytes = declType == Rx2ModelLayout.TypeFloat32 ? 12 : 6;
        if (offset < 0 || offset + requiredBytes > data.Length)
        {
            return Vector3.Zero;
        }

        return declType == Rx2ModelLayout.TypeFloat32
            ? new Vector3(
                BitConverterBigEndianSingle(data, offset),
                BitConverterBigEndianSingle(data, offset + 4),
                BitConverterBigEndianSingle(data, offset + 8))
            : Rx2HalfFloat.DecodeVector3(data, offset);
    }

    private static Vector2 ReadUv(byte[] data, int offset, int declType)
    {
        switch (declType)
        {
            case Rx2ModelLayout.TypeUvFloat:
                if (offset + 8 > data.Length)
                {
                    return Vector2.Zero;
                }

                return new Vector2(BitConverterBigEndianSingle(data, offset), BitConverterBigEndianSingle(data, offset + 4));

            case Rx2ModelLayout.TypeUvHalfFloat:
                if (offset + 4 > data.Length)
                {
                    return Vector2.Zero;
                }

                return new Vector2(Rx2HalfFloat.Decode(data[offset], data[offset + 1]), Rx2HalfFloat.Decode(data[offset + 2], data[offset + 3]));

            case Rx2ModelLayout.TypeUvUShort:
                if (offset + 4 > data.Length)
                {
                    return Vector2.Zero;
                }

                return new Vector2(
                    BigEndianReader.ReadUInt16BEAt(data, offset) / 65535f,
                    BigEndianReader.ReadUInt16BEAt(data, offset + 2) / 65535f);

            case Rx2ModelLayout.TypeUvShort:
                if (offset + 4 > data.Length)
                {
                    return Vector2.Zero;
                }

                return new Vector2(
                    (BigEndianReader.ReadInt16BEAt(data, offset) + 32768) / 65535f,
                    (BigEndianReader.ReadInt16BEAt(data, offset + 2) + 32768) / 65535f);

            default:
                return Vector2.Zero;
        }
    }

    private static float BitConverterBigEndianSingle(byte[] data, int offset)
    {
        Span<byte> buffer = stackalloc byte[4];
        buffer[0] = data[offset + 3];
        buffer[1] = data[offset + 2];
        buffer[2] = data[offset + 1];
        buffer[3] = data[offset];
        return BitConverter.ToSingle(buffer);
    }
}
