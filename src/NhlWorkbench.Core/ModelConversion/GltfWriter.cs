using System.Numerics;
using NhlWorkbench.Core.Formats.Rx2;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Writes one or more RX2 submeshes to a glTF 2.0 file (.gltf or .glb, chosen by
/// <paramref name="path"/>'s extension) via SharpGLTF's scene-builder API. Each submesh
/// becomes its own named mesh/node -- named after <see cref="Rx2Mesh.Name"/>, mirroring
/// <see cref="ObjWriter"/> -- so <see cref="GltfReader"/> and the Model Viewer's submesh list
/// line up the same way they do for OBJ. Like ObjWriter, this only carries geometry and
/// material *names* (no baked texture images); texture extraction is the Texture Editor's job.
/// Unlike OBJ, glTF's UV origin is already top-left (V increases downward) -- the same
/// convention this game's own UVs use -- so, deliberately unlike <see cref="ObjWriter"/>,
/// no V-flip is applied here.
/// </summary>
public static class GltfWriter
{
    public static void Write(string path, IReadOnlyList<Rx2Mesh> meshes, IReadOnlyDictionary<string, Rx2Material>? materialsByMeshName = null)
    {
        var scene = new SceneBuilder();

        foreach (Rx2Mesh mesh in meshes)
        {
            Rx2Material? material = materialsByMeshName is not null && materialsByMeshName.TryGetValue(mesh.Name, out Rx2Material? found) ? found : null;
            var materialBuilder = new MaterialBuilder(material?.MaterialName ?? $"mesh_{mesh.Name}");

            var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1>(mesh.Name);
            IPrimitiveBuilder primitive = meshBuilder.UsePrimitive(materialBuilder);

            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> MakeVertex(int index)
            {
                Rx2Vertex v = mesh.Vertices[index];
                return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                    new VertexPositionNormal(v.Position, v.Normal),
                    new VertexTexture1(v.Uv));
            }

            for (int t = 0; t + 2 < mesh.TriangleIndices.Length; t += 3)
            {
                primitive.AddTriangle(
                    MakeVertex(mesh.TriangleIndices[t]),
                    MakeVertex(mesh.TriangleIndices[t + 1]),
                    MakeVertex(mesh.TriangleIndices[t + 2]));
            }

            scene.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
        }

        SharpGLTF.Schema2.ModelRoot model = scene.ToGltf2();
        model.Save(path);
    }
}
