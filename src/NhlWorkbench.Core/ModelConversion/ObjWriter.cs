using System.Globalization;
using System.Text;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Writes a Wavefront OBJ (+ companion MTL) from one or more RX2 submeshes. Vertex/normal/UV
/// indices in OBJ are 1-based and shared across the whole file (not reset per object), so this
/// tracks a running offset as it writes each submesh in turn. OBJ's V texture-coordinate
/// convention has its origin at the bottom-left (V increases upward); this game's UVs -- like
/// most console-era Direct3D content -- have V increasing downward from the top-left, so V is
/// flipped (`1 - v`) on write. Both this and the coordinate/winding convention were confirmed
/// empirically via a real round-trip export/re-import rather than assumed.
/// </summary>
public static class ObjWriter
{
    /// <summary>
    /// Writes <paramref name="meshes"/> to <paramref name="objPath"/> plus a same-named
    /// <c>.mtl</c> file. <paramref name="materialsByMeshName"/> is optional -- when absent (or
    /// missing an entry for a given mesh), a placeholder material named "mesh_{name}" with no
    /// texture reference is emitted instead.
    /// </summary>
    public static void Write(string objPath, IReadOnlyList<Rx2Mesh> meshes, IReadOnlyDictionary<string, Rx2Material>? materialsByMeshName = null)
    {
        string mtlPath = Path.ChangeExtension(objPath, ".mtl");
        string mtlFileName = Path.GetFileName(mtlPath);

        var obj = new StringBuilder();
        obj.AppendLine("# Exported by NHL Legacy Modding Workbench");
        obj.AppendLine($"mtllib {mtlFileName}");

        var mtl = new StringBuilder();
        mtl.AppendLine("# Exported by NHL Legacy Modding Workbench");

        var writtenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int vertexOffset = 0;

        foreach (Rx2Mesh mesh in meshes)
        {
            string objectName = SanitizeName(mesh.Name);
            obj.AppendLine($"o {objectName}");

            foreach (Rx2Vertex v in mesh.Vertices)
            {
                obj.AppendLine($"v {F(v.Position.X)} {F(v.Position.Y)} {F(v.Position.Z)}");
            }

            foreach (Rx2Vertex v in mesh.Vertices)
            {
                obj.AppendLine($"vn {F(v.Normal.X)} {F(v.Normal.Y)} {F(v.Normal.Z)}");
            }

            foreach (Rx2Vertex v in mesh.Vertices)
            {
                obj.AppendLine($"vt {F(v.Uv.X)} {F(1f - v.Uv.Y)}");
            }

            Rx2Material? material = materialsByMeshName is not null && materialsByMeshName.TryGetValue(mesh.Name, out Rx2Material? found) ? found : null;
            string materialName = material is not null ? SanitizeName(material.MaterialName) : $"mesh_{objectName}";
            obj.AppendLine($"usemtl {materialName}");

            if (writtenMaterials.Add(materialName))
            {
                mtl.AppendLine($"newmtl {materialName}");
                mtl.AppendLine("Kd 0.800 0.800 0.800");
                if (material?.DiffuseTextureName is { Length: > 0 } diffuse)
                {
                    mtl.AppendLine($"map_Kd {diffuse}");
                }

                if (material?.NormalTextureName is { Length: > 0 } normal)
                {
                    mtl.AppendLine($"norm {normal}");
                }

                if (material?.SpecularTextureName is { Length: > 0 } specular)
                {
                    mtl.AppendLine($"map_Ks {specular}");
                }

                mtl.AppendLine();
            }

            for (int t = 0; t + 2 < mesh.TriangleIndices.Length; t += 3)
            {
                int a = mesh.TriangleIndices[t] + 1 + vertexOffset;
                int b = mesh.TriangleIndices[t + 1] + 1 + vertexOffset;
                int c = mesh.TriangleIndices[t + 2] + 1 + vertexOffset;
                obj.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }

            vertexOffset += mesh.Vertices.Length;
        }

        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(mtlPath, mtl.ToString());
    }

    private static string F(float value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private static string SanitizeName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsWhiteSpace(c) || c is '/' or '\\' ? '_' : c;
        }

        string sanitized = new(buffer);
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }
}
