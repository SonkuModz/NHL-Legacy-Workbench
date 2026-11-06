namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Everything <see cref="Rx2ModelWriter"/> needs to replace one submesh's vertex/index buffers
/// without touching any other byte in the file -- the mesh analog of
/// <c>TextureEntry.WriteBackMetadata</c>. Captured once at parse time by
/// <see cref="Rx2ModelReader"/> so the writer never has to re-walk the file-table resolution
/// chain (object-type-mesh -> buffer-info -> buffer index) a second time.
/// </summary>
public sealed class Rx2MeshWriteBackInfo
{
    /// <summary>File-table index of the raw vertex-buffer entry (its Offset/Size fields get patched on write).</summary>
    public required int VertexBufferEntryIndex { get; init; }

    /// <summary>File-table index of the raw index-buffer entry (its Offset/Size fields get patched on write).</summary>
    public required int FaceBufferEntryIndex { get; init; }

    /// <summary>Absolute offset of the FInfo index-count field (must be patched to the new triangle-list index count).</summary>
    public required int IndexCountFieldAbsoluteOffset { get; init; }

    /// <summary>Absolute offset of the FInfo index-buffer byte-size field (must be patched alongside the buffer's file-table Size, or the game keeps using the stale size).</summary>
    public required int IndexBufferSizeFieldAbsoluteOffset { get; init; }

    /// <summary>Absolute offset of the VInfo vertex-buffer byte-size field (stores <see cref="Rx2ModelLayout.VertexInfoSizeFlag"/> | (size + 2)).</summary>
    public required int VertexBufferSizeFieldAbsoluteOffset { get; init; }

    /// <summary>Absolute offset of the VInfo vertex-count field. This is the count the game actually renders with -- leaving it stale after a vertex-count-changing import is what exploded stadium geometry in-game.</summary>
    public required int VertexCountFieldAbsoluteOffset { get; init; }

    /// <summary>Vertex count the submesh had when parsed (from the VInfo count field), used to slice the original vertex buffer when preserving untouched attributes.</summary>
    public required int OriginalVertexCount { get; init; }

    /// <summary>Absolute offset of the FxRenderableSimple's primitiveType field -- always patched to <see cref="Rx2ModelLayout.PrimitiveTypeTriangleList"/> on write, regardless of the original primitive type, so the writer never needs a triangle-list-to-strip re-encoder.</summary>
    public required int PrimitiveTypeFieldAbsoluteOffset { get; init; }

    /// <summary>Per-vertex byte stride -- preserved as-is; the writer never changes it.</summary>
    public required int Stride { get; init; }

    public required int? PositionOffset { get; init; }

    public required int PositionType { get; init; }

    public required int? UvOffset { get; init; }

    public required int UvType { get; init; }

    /// <summary>Same header-size base used when this submesh's buffers were originally read (0 for files with a zero total-data-size field).</summary>
    public required int HeaderSize { get; init; }
}
