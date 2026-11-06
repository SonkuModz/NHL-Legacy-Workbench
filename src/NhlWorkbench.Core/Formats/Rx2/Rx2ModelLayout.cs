namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Named offsets for the RX2-as-mesh layout, transcribed directly from the shipped
/// Noesis Python model importer (References/Noesis/plugins/python/mdl_NHLLegacy_X360_rx2.py,
/// lines 46-338) rather than a secondhand summary -- the resolution chain here is a
/// sequence of "read N ints, some of which are indices into the file table itself
/// (not byte offsets)", which is easy to get subtly wrong without the exact source.
/// </summary>
public static class Rx2ModelLayout
{
    /// <summary>File-table type tag for a submesh ("EA::FxShader::FxRenderableSimple").</summary>
    public const uint TypeTagFxRenderableSimple = 15663108; // 0xEF0004

    /// <summary>File-table type tag for the section mapping each renderable's shader index to a material index ("MODELRENDER_ARENAID"). Only present in combined mesh+texture (stadium/arena) containers -- plain mesh files (jersey/player/glove/etc) carry no material data at all.</summary>
    public const uint TypeTagModelRenderArenaId = 15400963; // 0xEB0003

    /// <summary>File-table type tag for a material entry ("EA::FxShader::FxMaterial").</summary>
    public const uint TypeTagFxMaterial = 15663109; // 0xEF0005

    // At a FxRenderableSimple entry's own OffsetField: 3 x int32, then a null-terminated name.
    public const int RenderableObjectTypeMeshIndexOffset = 0x00; // fileList index of the OBJECTTYPE_MESH entry
    public const int RenderableVertexBufferDescIndexOffset = 0x04; // fileList index of the vertex-buffer descriptor entry
    public const int RenderablePrimitiveTypeOffset = 0x08;

    public const int PrimitiveTypeTriangleList = 4;
    public const int PrimitiveTypeTriangleStrip = 6;

    // At the OBJECTTYPE_MESH entry's OffsetField: 6 x int32.
    public const int ObjectTypeMeshIndexBufferInfoIndexOffset = 4 * 4; // OBJECTTYPE_MESH[4]
    public const int ObjectTypeMeshVertexBufferInfoIndexOffset = 4 * 5; // OBJECTTYPE_MESH[5]

    /// <summary>Both the index-buffer-info (FInfo) and vertex-buffer-info (VInfo) descriptors start this many bytes after their entry's own OffsetField.</summary>
    public const int BufferInfoHeaderSkip = 24;

    // FInfo: 3 x int32 at (indexBufferInfoEntry.OffsetField + BufferInfoHeaderSkip).
    public const int FaceInfoBufferIndexOffset = 0; // fileList index of the raw index-buffer entry
    public const int FaceInfoBufferSizeOffset = 4; // byte size of the index buffer (== its file-table entry's Size == AlignUp(indexCount*2, 16); verified 157/157 submeshes in a real stadium container)
    public const int FaceInfoIndexCountOffset = 8; // index count

    // VInfo: 5 x int32 at (vertexBufferInfoEntry.OffsetField + BufferInfoHeaderSkip).
    // Field semantics verified against every one of the 157 submeshes in a real stadium
    // container (References/Stadium RX2 Container/stadium_26_container_0.rx2):
    //   [0] = (fileList index of the vertex buffer) << 2
    //   [1] = VertexInfoSizeFlag | (vertex-buffer byte size + 2), where the byte size equals
    //         the buffer entry's file-table Size field (== AlignUp(vertexCount*stride, 32))
    //   [2] = (stride << 16) | 0x200
    //   [3] = vertex count (authoritative -- can be less than bufferSize/stride when the
    //         buffer carries alignment padding, e.g. 3539 real vertices in a 3540-slot buffer)
    //   [4] = vertex-buffer fileList index - 1
    // Leaving [1]/[3] stale while resizing the buffers is what exploded stadium geometry
    // in-game: the renderer kept drawing with the old vertex count.
    public const int VertexInfoPackedBufferIndexOffset = 0; // (fileList index of the vertex buffer) << 2
    public const int VertexInfoBufferSizeOffset = 4; // VertexInfoSizeFlag | (byte size + 2)
    public const int VertexInfoVertexCountOffset = 12; // vertex count

    public const uint VertexInfoSizeFlag = 0x10000000;

    /// <summary>Vertex-buffer entry sizes in real files are always 32-byte multiples of the vertex data.</summary>
    public const int VertexBufferSlotAlignment = 32;

    /// <summary>Index-buffer entry sizes in real files are always AlignUp(indexCount*2, 16).</summary>
    public const int IndexBufferSlotAlignment = 16;

    /// <summary>Every GPU buffer offset observed in real files is at least 16-byte aligned, so appended buffers must be too.</summary>
    public const int GpuBufferOffsetAlignment = 16;

    // Vertex-buffer descriptor (VBinfo) at the vertex-buffer-descriptor entry's OffsetField:
    // 2 unknown int32 (8 bytes), then element-count byte, stride byte, 2 unknown bytes, one
    // unknown int32 (16 bytes total header), then element-count x 12-byte element declarations.
    public const int VertexDescElementCountOffset = 8; // byte
    public const int VertexDescStrideOffset = 9; // byte
    public const int VertexDescElementListOffset = 16;
    public const int VertexDescElementSize = 12; // byteOffset(int32) + d3dType(int32) + d3dUsage(int32)

    // D3D declaration usage codes (only the ones this reader consumes -- position and the
    // first UV channel; bone weights/indices and additional UV channels exist in the format
    // but are out of scope, see Explicit non-goals).
    public const int UsagePosition = 1;
    public const int UsageTexCoord0 = 327686;

    // D3D declaration type codes.
    public const int TypeFloat32 = 2761657;
    public const int TypeHalfFloat = 1712992;
    public const int TypeUvFloat = 2892709;
    public const int TypeUvHalfFloat = 2892639;
    public const int TypeUvUShort = 2891865;
    public const int TypeUvShort = 2892121;
}
