using System.Globalization;
using System.Text;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Writes one or more RX2 submeshes to a minimal, hand-rolled ASCII FBX 7.3 file. Export-only,
/// deliberately -- there is no free, verified-production-ready two-way FBX library for .NET (see
/// the plan's format-scope decision), so FBX support here is a one-way "get geometry into
/// Blender/Maya/3ds Max" path; re-importing an FBX-edited model happens via OBJ or glTF export
/// from the external tool instead.
///
/// Follows the well-documented FBX 7.x ASCII grammar (FBXHeaderExtension / GlobalSettings /
/// Objects / Connections) rather than any single vendor SDK. Per submesh: one Geometry node
/// (positions in <c>Vertices</c>, triangles in <c>PolygonVertexIndex</c> using FBX's
/// end-of-polygon convention of bitwise-complementing the last index of each polygon), one
/// Model node, and one Material node, wired together in <c>Connections</c>. Normals/UVs use
/// <c>MappingInformationType: "ByVertice"</c> / <c>ReferenceInformationType: "Direct"</c> so they
/// align 1:1 with the per-vertex arrays already on <see cref="Rx2Vertex"/> -- no separate
/// per-polygon-corner duplication needed.
///
/// IMPORTANT: this environment has no Blender/Maya/3ds Max installation available to actually
/// open the file in, so verification here is structural (well-formed grammar, balanced braces,
/// correct counts) rather than a real DCC-tool import test. Treat a real import in Blender/Maya
/// as the true acceptance test before relying on this for a production modding workflow.
/// </summary>
public static class FbxAsciiWriter
{
    public static void Write(string path, IReadOnlyList<Rx2Mesh> meshes, IReadOnlyDictionary<string, Rx2Material>? materialsByMeshName = null)
    {
        var sb = new StringBuilder();
        WriteHeader(sb);
        WriteGlobalSettings(sb);

        sb.AppendLine("Objects:  {");
        var connections = new List<(long Child, long Parent)>();

        for (int i = 0; i < meshes.Count; i++)
        {
            Rx2Mesh mesh = meshes[i];
            long geometryId = 1_000_000_000 + (i * 3L);
            long modelId = geometryId + 1;
            long materialId = geometryId + 2;

            Rx2Material? material = materialsByMeshName is not null && materialsByMeshName.TryGetValue(mesh.Name, out Rx2Material? found) ? found : null;
            string meshName = SanitizeName(mesh.Name);
            string materialName = material is not null ? SanitizeName(material.MaterialName) : $"mesh_{meshName}";

            WriteGeometry(sb, geometryId, meshName, mesh);
            WriteModel(sb, modelId, meshName);
            WriteMaterial(sb, materialId, materialName);

            connections.Add((geometryId, modelId));
            connections.Add((modelId, 0));
            connections.Add((materialId, modelId));
        }

        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("Connections:  {");
        foreach ((long child, long parent) in connections)
        {
            sb.Append("\tC: \"OO\",").Append(child).Append(',').Append(parent).AppendLine();
        }

        sb.AppendLine("}");

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine("; FBX 7.3.0 project file");
        sb.AppendLine("; Exported by NHL Legacy Modding Workbench (ASCII, geometry export-only)");
        sb.AppendLine("; ----------------------------------------------------");
        sb.AppendLine();
        sb.AppendLine("FBXHeaderExtension:  {");
        sb.AppendLine("\tFBXHeaderVersion: 1003");
        sb.AppendLine("\tFBXVersion: 7300");
        sb.AppendLine("\tCreator: \"NHL Legacy Modding Workbench\"");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteGlobalSettings(StringBuilder sb)
    {
        sb.AppendLine("GlobalSettings:  {");
        sb.AppendLine("\tVersion: 1000");
        sb.AppendLine("\tProperties70:  {");
        sb.AppendLine("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2");
        sb.AppendLine("\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0");
        sb.AppendLine("\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteGeometry(StringBuilder sb, long geometryId, string meshName, Rx2Mesh mesh)
    {
        int vertexCount = mesh.Vertices.Length;
        int polygonIndexCount = mesh.TriangleIndices.Length;

        sb.Append("\tGeometry: ").Append(geometryId).Append(", \"Geometry::").Append(meshName).AppendLine("\", \"Mesh\" {");

        sb.Append("\t\tVertices: *").Append(vertexCount * 3).AppendLine(" {");
        sb.Append("\t\t\ta: ");
        for (int v = 0; v < vertexCount; v++)
        {
            if (v > 0)
            {
                sb.Append(',');
            }

            Rx2Vertex vertex = mesh.Vertices[v];
            sb.Append(F(vertex.Position.X)).Append(',').Append(F(vertex.Position.Y)).Append(',').Append(F(vertex.Position.Z));
        }

        sb.AppendLine();
        sb.AppendLine("\t\t}");

        sb.Append("\t\tPolygonVertexIndex: *").Append(polygonIndexCount).AppendLine(" {");
        sb.Append("\t\t\ta: ");
        for (int t = 0; t + 2 < mesh.TriangleIndices.Length; t += 3)
        {
            if (t > 0)
            {
                sb.Append(',');
            }

            int a = mesh.TriangleIndices[t];
            int b = mesh.TriangleIndices[t + 1];
            int c = mesh.TriangleIndices[t + 2];
            sb.Append(a).Append(',').Append(b).Append(',').Append(EndOfPolygon(c));
        }

        sb.AppendLine();
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tGeometryVersion: 124");

        sb.AppendLine("\t\tLayerElementNormal: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByVertice\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        sb.Append("\t\t\tNormals: *").Append(vertexCount * 3).AppendLine(" {");
        sb.Append("\t\t\t\ta: ");
        for (int v = 0; v < vertexCount; v++)
        {
            if (v > 0)
            {
                sb.Append(',');
            }

            Rx2Vertex vertex = mesh.Vertices[v];
            sb.Append(F(vertex.Normal.X)).Append(',').Append(F(vertex.Normal.Y)).Append(',').Append(F(vertex.Normal.Z));
        }

        sb.AppendLine();
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        sb.AppendLine("\t\tLayerElementUV: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"UVMap\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByVertice\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        sb.Append("\t\t\tUV: *").Append(vertexCount * 2).AppendLine(" {");
        sb.Append("\t\t\t\ta: ");
        for (int v = 0; v < vertexCount; v++)
        {
            if (v > 0)
            {
                sb.Append(',');
            }

            Rx2Vertex vertex = mesh.Vertices[v];
            // Same bottom-left-origin V-flip as ObjWriter -- FBX inherits Maya's UV convention
            // (V increases upward), the opposite of this game's top-left-origin UVs.
            sb.Append(F(vertex.Uv.X)).Append(',').Append(F(1f - vertex.Uv.Y));
        }

        sb.AppendLine();
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        sb.AppendLine("\t\tLayerElementMaterial: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"AllSame\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"IndexToDirect\"");
        sb.AppendLine("\t\t\tMaterials: *1 {");
        sb.AppendLine("\t\t\t\ta: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        sb.AppendLine("\t\tLayer: 0 {");
        sb.AppendLine("\t\t\tVersion: 100");
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.AppendLine("\t\t\t\tType: \"LayerElementNormal\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.AppendLine("\t\t\t\tType: \"LayerElementUV\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.AppendLine("\t\t\t\tType: \"LayerElementMaterial\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
        sb.AppendLine("\t\t}");

        sb.AppendLine("\t}");
    }

    private static void WriteModel(StringBuilder sb, long modelId, string meshName)
    {
        sb.Append("\tModel: ").Append(modelId).Append(", \"Model::").Append(meshName).AppendLine("\", \"Mesh\" {");
        sb.AppendLine("\t\tVersion: 232");
        sb.AppendLine("\t\tProperties70:  {");
        sb.AppendLine("\t\t\tP: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0");
        sb.AppendLine("\t\t\tP: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0");
        sb.AppendLine("\t\t\tP: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tShading: T");
        sb.AppendLine("\t\tCulling: \"CullingOff\"");
        sb.AppendLine("\t}");
    }

    private static void WriteMaterial(StringBuilder sb, long materialId, string materialName)
    {
        sb.Append("\tMaterial: ").Append(materialId).Append(", \"Material::").Append(materialName).AppendLine("\", \"\" {");
        sb.AppendLine("\t\tVersion: 102");
        sb.AppendLine("\t\tShadingModel: \"phong\"");
        sb.AppendLine("\t\tMultiLayer: 0");
        sb.AppendLine("\t\tProperties70:  {");
        sb.AppendLine("\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",0.8,0.8,0.8");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");
    }

    /// <summary>FBX marks the last index of each polygon by bitwise-complementing it (so it's always negative, even for index 0).</summary>
    private static int EndOfPolygon(int index) => ~index;

    private static string F(float value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    private static string SanitizeName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsWhiteSpace(c) || c is '/' or '\\' or '"' ? '_' : c;
        }

        string sanitized = new(buffer);
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }
}
