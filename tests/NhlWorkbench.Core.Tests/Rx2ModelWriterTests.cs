using System.Linq;
using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// <see cref="Rx2ModelWriter"/> must follow the exact same non-destructive discipline that
/// fixed the stadium-texture corruption bug (see Rx2StadiumRebuildTests) -- combined mesh+
/// texture containers share their GPU-data region, so writing one submesh's buffers must
/// never disturb any other submesh or any texture in the same file. These tests exercise
/// real stadium files (the only real sample files with resolvable <see
/// cref="Rx2MeshWriteBackInfo"/>) end to end: parse, re-encode the same or resized geometry,
/// write, reparse, and assert everything untouched stayed byte-identical.
/// </summary>
public class Rx2ModelWriterTests
{
    private static string? FindRealStadiumFile()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return null;
        }

        return System.IO.Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "stadium_*_container_0.rx2").FirstOrDefault();
    }

    private static (byte[] Original, Rx2Model Model, int TargetIndex) LoadWritableSubmesh(string path)
    {
        byte[] original = System.IO.File.ReadAllBytes(path);
        Rx2Model model = Rx2ModelReader.Parse(original, path).Value!;
        int targetIndex = -1;
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (model.Meshes[i].WriteBackInfo is not null)
            {
                targetIndex = i;
                break;
            }
        }

        return (original, model, targetIndex);
    }

    [Fact]
    public void ReplaceSubmesh_WithIdenticalGeometry_IsByteEquivalentForEveryOtherSubmeshAndTexture()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        (byte[] original, Rx2Model model, int targetIndex) = LoadWritableSubmesh(path);
        if (targetIndex < 0)
        {
            return;
        }

        Rx2Mesh target = model.Meshes[targetIndex];
        var positions = target.Vertices.Select(v => v.Position).ToArray();
        var uvs = target.Vertices.Select(v => v.Uv).ToArray();

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, positions, uvs, target.TriangleIndices);
        Assert.True(result.IsSuccess, result.Error);

        byte[] rewritten = result.Value!;
        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, path).Value!;

        Assert.Equal(model.Meshes.Count, reparsed.Meshes.Count);

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            AssertPositionsEqual(model.Meshes[i], reparsed.Meshes[i]);
        }

        AssertTexturesUnchanged(original, rewritten, path);
    }

    [Fact]
    public void ReplaceSubmesh_WithFewerVertices_FitsInPlaceAndLeavesOthersUntouched()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        (byte[] original, Rx2Model model, int targetIndex) = LoadWritableSubmesh(path);
        if (targetIndex < 0)
        {
            return;
        }

        Rx2Mesh target = model.Meshes[targetIndex];
        if (target.Vertices.Length < 4)
        {
            return;
        }

        // Half the vertices, a single triangle -- guaranteed to be <= the original buffer size.
        int keep = Math.Max(3, target.Vertices.Length / 2);
        var positions = target.Vertices.Take(keep).Select(v => v.Position).ToArray();
        var uvs = target.Vertices.Take(keep).Select(v => v.Uv).ToArray();
        var triangles = new[] { 0, 1, 2 };

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, positions, uvs, triangles);
        Assert.True(result.IsSuccess, result.Error);

        byte[] rewritten = result.Value!;
        Assert.Equal(original.Length, rewritten.Length);

        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, path).Value!;
        Assert.Equal(keep, reparsed.Meshes[targetIndex].Vertices.Length);
        Assert.Equal(positions[0], reparsed.Meshes[targetIndex].Vertices[0].Position);
        Assert.Equal(positions[1], reparsed.Meshes[targetIndex].Vertices[1].Position);
        Assert.Equal(positions[2], reparsed.Meshes[targetIndex].Vertices[2].Position);

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (i == targetIndex)
            {
                continue;
            }

            AssertPositionsEqual(model.Meshes[i], reparsed.Meshes[i]);
        }

        AssertTexturesUnchanged(original, rewritten, path);
    }

    [Fact]
    public void ReplaceSubmesh_WithManyMoreVertices_AppendsPastEndOfFileWithoutDisturbingOthers()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        (byte[] original, Rx2Model model, int targetIndex) = LoadWritableSubmesh(path);
        if (targetIndex < 0)
        {
            return;
        }

        Rx2Mesh target = model.Meshes[targetIndex];

        // Repeat the original geometry enough times that the new vertex buffer can't possibly
        // fit the original slot, forcing the append path.
        int repeats = 20;
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        for (int r = 0; r < repeats; r++)
        {
            int offset = positions.Count;
            positions.AddRange(target.Vertices.Select(v => v.Position));
            uvs.AddRange(target.Vertices.Select(v => v.Uv));
            triangles.AddRange(target.TriangleIndices.Select(idx => idx + offset));
        }

        if (positions.Count > Rx2ModelWriter.MaxVertexCount)
        {
            return;
        }

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, positions, uvs, triangles);
        Assert.True(result.IsSuccess, result.Error);

        byte[] rewritten = result.Value!;
        Assert.True(rewritten.Length > original.Length, "Expected the file to grow when the new buffer can't fit its original slot.");

        Rx2Model reparsed = Rx2ModelReader.Parse(rewritten, path).Value!;
        Assert.Equal(positions.Count, reparsed.Meshes[targetIndex].Vertices.Length);

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (i == targetIndex)
            {
                continue;
            }

            AssertPositionsEqual(model.Meshes[i], reparsed.Meshes[i]);
        }

        AssertTexturesUnchanged(original, rewritten, path);
    }

    [Fact]
    public void ReplaceSubmesh_WithMoreThan65535Vertices_FailsWithoutModifyingAnything()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        (byte[] original, Rx2Model model, int targetIndex) = LoadWritableSubmesh(path);
        if (targetIndex < 0)
        {
            return;
        }

        Rx2Mesh target = model.Meshes[targetIndex];
        int tooMany = Rx2ModelWriter.MaxVertexCount + 1;
        var positions = new Vector3[tooMany];
        var uvs = new Vector2[tooMany];

        Result<byte[]> result = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, positions, uvs, new[] { 0, 1, 2 });

        Assert.False(result.IsSuccess);
        Assert.Contains("65535", result.Error);
    }

    private static void AssertPositionsEqual(Rx2Mesh before, Rx2Mesh after)
    {
        Assert.Equal(before.Vertices.Length, after.Vertices.Length);
        for (int v = 0; v < before.Vertices.Length; v++)
        {
            Assert.Equal(before.Vertices[v].Position, after.Vertices[v].Position);
        }
    }

    private static void AssertTexturesUnchanged(byte[] before, byte[] after, string path)
    {
        var beforeTextures = Rx2Reader.Parse(before, path);
        var afterTextures = Rx2Reader.Parse(after, path);
        Assert.True(beforeTextures.IsSuccess);
        Assert.True(afterTextures.IsSuccess);
        Assert.Equal(beforeTextures.Value!.Textures.Count, afterTextures.Value!.Textures.Count);

        for (int i = 0; i < beforeTextures.Value!.Textures.Count; i++)
        {
            Assert.Equal(beforeTextures.Value!.Textures[i].LinearPixelData, afterTextures.Value!.Textures[i].LinearPixelData);
        }
    }
}
