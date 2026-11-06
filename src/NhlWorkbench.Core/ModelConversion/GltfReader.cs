using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using SharpGLTF.Schema2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Reads a glTF 2.0 file (.gltf or .glb) back into per-mesh geometry via SharpGLTF. Iterates
/// *nodes* rather than meshes directly, and named/keyed by node name (falling back to the
/// underlying mesh's name) -- matching how <see cref="GltfWriter"/> names each RX2 submesh's
/// node/mesh after <c>Rx2Mesh.Name</c>, and matching Blender's own behavior of naming the
/// exported node after the object (which is what a user actually renames/keys off of, not the
/// underlying mesh data-block).
///
/// Critically, each primitive's raw vertex positions are in the *node's local space*, not the
/// final scene-space the RX2 file expects -- Blender's glTF exporter (and others) commonly bake
/// a rotation into the node transform to convert from Blender's Z-up convention to glTF's Y-up
/// convention, even when the node has no explicit rotation set in Blender's own UI. Confirmed
/// directly against a real Blender export: a raw local-space vertex of
/// (-10.20, -3.40, -22.75) only lands inside the original RX2 submesh's actual bounds after
/// applying the node's world matrix, becoming (-10.20, 22.75, -3.40) -- reading raw positions
/// without this transform silently imports the mesh rotated ~90 degrees and in the wrong
/// position. So every position is transformed by <see cref="Node.WorldMatrix"/> before being
/// returned. Normals aren't transformed (or even read) since RX2 write-back recomputes normals
/// on the C# side, not from the imported mesh.
/// </summary>
public static class GltfReader
{
    public static Result<IReadOnlyList<ImportedSubmesh>> Read(string path)
    {
        try
        {
            ModelRoot model = ModelRoot.Load(path);
            var submeshes = new List<ImportedSubmesh>();
            int fallbackIndex = 0;

            foreach (Node node in model.LogicalNodes)
            {
                Mesh? mesh = node.Mesh;
                if (mesh is null)
                {
                    continue;
                }

                string name = !string.IsNullOrEmpty(node.Name) ? node.Name : !string.IsNullOrEmpty(mesh.Name) ? mesh.Name : $"mesh_{fallbackIndex}";
                fallbackIndex++;

                Matrix4x4 worldMatrix = node.WorldMatrix;

                var positions = new List<Vector3>();
                var uvs = new List<Vector2>();
                var triangles = new List<int>();

                foreach (MeshPrimitive primitive in mesh.Primitives)
                {
                    IList<Vector3> primitivePositions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
                        ?? throw new InvalidOperationException($"Mesh '{name}' has a primitive with no POSITION accessor.");
                    IList<Vector2>? primitiveUvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                    int vertexOffset = positions.Count;
                    for (int v = 0; v < primitivePositions.Count; v++)
                    {
                        positions.Add(Vector3.Transform(primitivePositions[v], worldMatrix));
                        uvs.Add(primitiveUvs is not null && v < primitiveUvs.Count ? primitiveUvs[v] : Vector2.Zero);
                    }

                    foreach ((int a, int b, int c) in primitive.GetTriangleIndices())
                    {
                        triangles.Add(a + vertexOffset);
                        triangles.Add(b + vertexOffset);
                        triangles.Add(c + vertexOffset);
                    }
                }

                if (positions.Count > 0)
                {
                    submeshes.Add(new ImportedSubmesh(name, positions.ToArray(), uvs.ToArray(), triangles.ToArray()));
                }
            }

            if (submeshes.Count == 0)
            {
                return Result.Failure<IReadOnlyList<ImportedSubmesh>>("No usable mesh geometry found in the glTF file.");
            }

            return Result.Success<IReadOnlyList<ImportedSubmesh>>(submeshes);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<ImportedSubmesh>>($"Failed to parse glTF file: {ex.Message}", ex);
        }
    }
}
