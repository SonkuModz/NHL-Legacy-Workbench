using System.Numerics;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>One submesh (one FxRenderableSimple file-table entry) -- WPF-free so Core has no rendering dependency.</summary>
public sealed class Rx2Mesh
{
    public required string Name { get; init; }

    public required Rx2Vertex[] Vertices { get; init; }

    /// <summary>Flat list, 3 entries per triangle, already expanded from whatever primitive type (list or strip) the file used.</summary>
    public required int[] TriangleIndices { get; init; }

    /// <summary>Null when this submesh's buffer layout couldn't be resolved cleanly enough to support writing it back (matches whatever <see cref="Rx2ModelReader"/> already tolerates read-side); callers must treat a missing value as "not importable", not an error.</summary>
    public Rx2MeshWriteBackInfo? WriteBackInfo { get; init; }
}

public readonly record struct Rx2Vertex(Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector2 Uv);
