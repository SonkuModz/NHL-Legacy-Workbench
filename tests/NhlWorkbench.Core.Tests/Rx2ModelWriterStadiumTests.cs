using System.Linq;
using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using NhlWorkbench.Core.Utilities;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth regression tests for the stadium mesh write-back corruption bug (in-game:
/// giant exploded geometry + uniformly white arena after editing the powerring). Root causes,
/// established against the raw unedited container in References/Stadium RX2 Container:
///
/// 1. The writer zero-filled every vertex attribute it doesn't model (packed normals, vertex
///    colors, lightmap UV channels 2-5) -- stadium lighting lives in those, hence the white
///    arena. Fixed by seeding each new vertex from an original vertex before overwriting
///    position/UV1.
/// 2. The FInfo/VInfo descriptor blocks carry index-buffer-size, vertex-buffer-size, and
///    vertex-count fields (verified against all 157 submeshes of the real container) that the
///    writer never patched -- the game renders with those counts, so a count-changing import
///    made the GPU read past the real vertices, hence the exploded geometry.
///
/// The strongest assertion here: replacing a submesh with its own parsed geometry must leave
/// the whole 10.5 MB file byte-for-byte identical.
/// </summary>
public class Rx2ModelWriterStadiumTests
{
    private static (byte[] Original, Rx2Model Model, Rx2Mesh Target)? LoadListPrimitiveTarget()
    {
        if (!SampleFileCatalog.StadiumContainerSampleAvailable)
        {
            return null;
        }

        byte[] original = System.IO.File.ReadAllBytes(SampleFileCatalog.StadiumContainerSampleFile);
        Rx2Model model = Rx2ModelReader.Parse(original, SampleFileCatalog.StadiumContainerSampleFile).Value!;

        // The submesh the user actually edited when the corruption surfaced. It is already a
        // triangle list, so a no-op replace has no strip-to-list index inflation and can be
        // asserted byte-identical.
        Rx2Mesh target = model.Meshes.First(m =>
            m.Name.Contains("powerring", System.StringComparison.OrdinalIgnoreCase) && m.WriteBackInfo is not null);
        return (original, model, target);
    }

    [Fact]
    public void ReplaceSubmesh_WithOwnParsedGeometry_IsByteIdenticalAcrossTheWholeFile()
    {
        if (LoadListPrimitiveTarget() is not (byte[] original, Rx2Model _, Rx2Mesh target))
        {
            return;
        }

        var positions = target.Vertices.Select(v => v.Position).ToArray();
        var uvs = target.Vertices.Select(v => v.Uv).ToArray();

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, positions, uvs, target.TriangleIndices);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(original, result.Value);
    }

    [Fact]
    public void ReplaceSubmesh_NoOp_OnEveryWritableSubmesh_PreservesUnmodeledAttributeBytes()
    {
        if (!SampleFileCatalog.StadiumContainerSampleAvailable)
        {
            return;
        }

        byte[] original = System.IO.File.ReadAllBytes(SampleFileCatalog.StadiumContainerSampleFile);
        Rx2Model model = Rx2ModelReader.Parse(original, SampleFileCatalog.StadiumContainerSampleFile).Value!;

        foreach (Rx2Mesh mesh in model.Meshes.Where(m => m.WriteBackInfo is not null))
        {
            Rx2MeshWriteBackInfo info = mesh.WriteBackInfo!;
            var positions = mesh.Vertices.Select(v => v.Position).ToArray();
            var uvs = mesh.Vertices.Select(v => v.Uv).ToArray();

            Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, info, positions, uvs, mesh.TriangleIndices);
            Assert.True(result.IsSuccess, $"{mesh.Name}: {result.Error}");
            byte[] rewritten = result.Value!;

            // The vertex buffer (located via the rewritten file's own table, since a
            // strip-to-list conversion can move the index buffer) must be byte-identical to
            // the original one for every real vertex -- positions and UV1 because the
            // encoders are lossless, everything else because it must be template-copied,
            // never zero-filled.
            (int offset, _) = ReadEntry(rewritten, info.VertexBufferEntryIndex);
            (int originalOffset, _) = ReadEntry(original, info.VertexBufferEntryIndex);
            int byteCount = positions.Length * info.Stride;
            Assert.True(
                rewritten.AsSpan(offset + info.HeaderSize, byteCount).SequenceEqual(
                    original.AsSpan(originalOffset + info.HeaderSize, byteCount)),
                $"{mesh.Name}: vertex buffer bytes changed on a no-op replace.");

            // Descriptor fields must stay consistent with what the file table now says.
            Assert.Equal(positions.Length, BigEndianReader.ReadInt32BEAt(rewritten, info.VertexCountFieldAbsoluteOffset));
            AssertDescriptorSizesMatchFileTable(rewritten, info);
        }
    }

    [Fact]
    public void ReplaceSubmesh_WithMoreVertices_PatchesDescriptorCountsAndSizes_AndInheritsAttributes()
    {
        if (LoadListPrimitiveTarget() is not (byte[] original, Rx2Model model, Rx2Mesh target))
        {
            return;
        }

        Rx2MeshWriteBackInfo info = target.WriteBackInfo!;

        // Duplicate the geometry so the buffers can't fit their slots (forces the append
        // path) and every duplicated vertex has an exact-position original to inherit from.
        var positions = new System.Collections.Generic.List<Vector3>();
        var uvs = new System.Collections.Generic.List<Vector2>();
        var triangles = new System.Collections.Generic.List<int>();
        for (int r = 0; r < 20; r++)
        {
            int offset = positions.Count;
            positions.AddRange(target.Vertices.Select(v => v.Position));
            uvs.AddRange(target.Vertices.Select(v => v.Uv));
            triangles.AddRange(target.TriangleIndices.Select(i => i + offset));
        }

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, info, positions, uvs, triangles);
        Assert.True(result.IsSuccess, result.Error);
        byte[] rewritten = result.Value!;
        Assert.True(rewritten.Length > original.Length);

        // Descriptor fields: the count the game renders with, and both size fields.
        Assert.Equal(positions.Count, BigEndianReader.ReadInt32BEAt(rewritten, info.VertexCountFieldAbsoluteOffset));
        Assert.Equal(triangles.Count, BigEndianReader.ReadInt32BEAt(rewritten, info.IndexCountFieldAbsoluteOffset));
        AssertDescriptorSizesMatchFileTable(rewritten, info);

        // Appended buffers must respect the GPU offset alignment real files use.
        (int vertexOffset, int vertexSize) = ReadEntry(rewritten, info.VertexBufferEntryIndex);
        (int faceOffset, int faceSize) = ReadEntry(rewritten, info.FaceBufferEntryIndex);
        Assert.Equal(0, (vertexOffset + info.HeaderSize) % Rx2ModelLayout.GpuBufferOffsetAlignment);
        Assert.Equal(0, (faceOffset + info.HeaderSize) % Rx2ModelLayout.GpuBufferOffsetAlignment);
        Assert.Equal(0, vertexSize % Rx2ModelLayout.VertexBufferSlotAlignment);
        Assert.Equal(0, faceSize % Rx2ModelLayout.IndexBufferSlotAlignment);

        // Every duplicated vertex must carry its source vertex's full byte image (positions
        // and UV1 are identical by construction; colors/normals/lightmap UVs by inheritance).
        // Same-slot vertices keep their own attributes; overflow duplicates inherit from the
        // FIRST original vertex sharing their exact position (the writer's mapping rule --
        // seam vertices can legitimately share a position).
        var firstByPosition = new System.Collections.Generic.Dictionary<Vector3, int>();
        for (int i = 0; i < target.Vertices.Length; i++)
        {
            firstByPosition.TryAdd(target.Vertices[i].Position, i);
        }

        // Position and UV1 are overwritten with the vertex's OWN values on top of the
        // template (two vertices can share a position but carry different UVs), so the
        // inheritance assertion covers only the bytes outside those two elements.
        (int originalVertexOffset, _) = ReadEntry(original, info.VertexBufferEntryIndex);
        int stride = info.Stride;
        int positionWidth = info.PositionType == Rx2ModelLayout.TypeFloat32 ? 12 : 6;
        int uvWidth = info.UvType == Rx2ModelLayout.TypeUvFloat ? 8 : 4;
        for (int v = 0; v < positions.Count; v++)
        {
            int source = v < target.Vertices.Length ? v : firstByPosition[positions[v]];
            for (int b = 0; b < stride; b++)
            {
                if (info.PositionOffset.HasValue && b >= info.PositionOffset.Value && b < info.PositionOffset.Value + positionWidth)
                {
                    continue;
                }

                if (info.UvOffset.HasValue && b >= info.UvOffset.Value && b < info.UvOffset.Value + uvWidth)
                {
                    continue;
                }

                Assert.True(
                    rewritten[vertexOffset + info.HeaderSize + (v * stride) + b]
                        == original[originalVertexOffset + info.HeaderSize + (source * stride) + b],
                    $"Vertex {v} did not inherit vertex {source}'s attribute byte {b}.");
            }
        }

        // Nothing else in the file may move: every other submesh reparses identically.
        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, SampleFileCatalog.StadiumContainerSampleFile).Value!;
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (model.Meshes[i].Name == target.Name && model.Meshes[i].Vertices.Length == target.Vertices.Length)
            {
                continue;
            }

            Assert.Equal(model.Meshes[i].Vertices.Length, reparsed.Meshes[i].Vertices.Length);
        }
    }

    [Fact]
    public void ReplaceSubmesh_WithFewerVertices_KeepsTheSlotAndPatchesTheCount()
    {
        if (LoadListPrimitiveTarget() is not (byte[] original, Rx2Model _, Rx2Mesh target))
        {
            return;
        }

        Rx2MeshWriteBackInfo info = target.WriteBackInfo!;
        int keep = target.Vertices.Length / 2;
        var positions = target.Vertices.Take(keep).Select(v => v.Position).ToArray();
        var uvs = target.Vertices.Take(keep).Select(v => v.Uv).ToArray();
        var triangles = new[] { 0, 1, 2 };

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, info, positions, uvs, triangles);
        Assert.True(result.IsSuccess, result.Error);
        byte[] rewritten = result.Value!;
        Assert.Equal(original.Length, rewritten.Length);

        // Slot fields untouched, descriptor count/sizes updated.
        Assert.Equal(ReadEntry(original, info.VertexBufferEntryIndex), ReadEntry(rewritten, info.VertexBufferEntryIndex));
        Assert.Equal(ReadEntry(original, info.FaceBufferEntryIndex), ReadEntry(rewritten, info.FaceBufferEntryIndex));
        Assert.Equal(keep, BigEndianReader.ReadInt32BEAt(rewritten, info.VertexCountFieldAbsoluteOffset));
        Assert.Equal(3, BigEndianReader.ReadInt32BEAt(rewritten, info.IndexCountFieldAbsoluteOffset));
        AssertDescriptorSizesMatchFileTable(rewritten, info);

        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, SampleFileCatalog.StadiumContainerSampleFile).Value!;
        Rx2Mesh reparsedTarget = reparsed.Meshes.First(m => m.Name == target.Name && m.Vertices.Length == keep);
        for (int v = 0; v < keep; v++)
        {
            Assert.Equal(target.Vertices[v].Position, reparsedTarget.Vertices[v].Position);
        }
    }

    [Fact]
    public void ReplaceSubmesh_ScaleInPlaceWithIdentityMapping_PreservesEveryVertexOwnAttributes()
    {
        if (LoadListPrimitiveTarget() is not (byte[] original, Rx2Model _, Rx2Mesh target))
        {
            return;
        }

        Rx2MeshWriteBackInfo info = target.WriteBackInfo!;

        // The Model Viewer's scale tool: same vertex order, moved positions, identity mapping.
        Vector3 min = target.Vertices[0].Position, max = min;
        foreach (Rx2Vertex v in target.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        Vector3 center = (min + max) * 0.5f;
        var positions = target.Vertices.Select(v => center + ((v.Position - center) * 3f)).ToArray();
        var uvs = target.Vertices.Select(v => v.Uv).ToArray();
        var identity = Enumerable.Range(0, positions.Length).ToArray();

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, info, positions, uvs, target.TriangleIndices, identity);
        Assert.True(result.IsSuccess, result.Error);
        byte[] rewritten = result.Value!;

        // Every byte outside the position element must be identical per-vertex -- with
        // position-based matching instead of the identity mapping, scaled vertices would
        // inherit whichever original vertex happened to be nearest, shuffling colors/normals.
        (int vertexOffset, _) = ReadEntry(rewritten, info.VertexBufferEntryIndex);
        (int originalVertexOffset, _) = ReadEntry(original, info.VertexBufferEntryIndex);
        int positionWidth = info.PositionType == Rx2ModelLayout.TypeFloat32 ? 12 : 6;
        for (int v = 0; v < positions.Length; v++)
        {
            for (int b = 0; b < info.Stride; b++)
            {
                if (info.PositionOffset.HasValue && b >= info.PositionOffset.Value && b < info.PositionOffset.Value + positionWidth)
                {
                    continue;
                }

                Assert.True(
                    rewritten[vertexOffset + info.HeaderSize + (v * info.Stride) + b]
                        == original[originalVertexOffset + info.HeaderSize + (v * info.Stride) + b],
                    $"Vertex {v} attribute byte {b} changed during an in-place scale.");
            }
        }

        // And the scaled positions decode back exactly 3x from center.
        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, SampleFileCatalog.StadiumContainerSampleFile).Value!;
        Rx2Mesh after = reparsed.Meshes.First(m => m.Name == target.Name);
        for (int v = 0; v < positions.Length; v++)
        {
            Assert.Equal(positions[v], after.Vertices[v].Position);
        }
    }

    /// <summary>Asserts the FInfo/VInfo size fields advertise exactly what the file table says --
    /// the invariant that held for 157/157 submeshes in the unedited container.</summary>
    private static void AssertDescriptorSizesMatchFileTable(byte[] data, Rx2MeshWriteBackInfo info)
    {
        (_, int vertexSize) = ReadEntry(data, info.VertexBufferEntryIndex);
        (_, int faceSize) = ReadEntry(data, info.FaceBufferEntryIndex);

        Assert.Equal(
            Rx2ModelLayout.VertexInfoSizeFlag | (uint)(vertexSize + 2),
            BigEndianReader.ReadUInt32BEAt(data, info.VertexBufferSizeFieldAbsoluteOffset));
        Assert.Equal(faceSize, BigEndianReader.ReadInt32BEAt(data, info.IndexBufferSizeFieldAbsoluteOffset));
    }

    private static (int Offset, int Size) ReadEntry(byte[] data, int entryIndex)
    {
        uint tableOffset = BigEndianReader.ReadUInt32BEAt(data, Rx2HeaderLayout.FileTableOffset);
        int entryBase = (int)tableOffset + (entryIndex * Rx2HeaderLayout.FileTableEntrySize);
        return (
            BigEndianReader.ReadInt32BEAt(data, entryBase + Rx2HeaderLayout.EntryOffsetField),
            BigEndianReader.ReadInt32BEAt(data, entryBase + Rx2HeaderLayout.EntrySizeField));
    }
}
