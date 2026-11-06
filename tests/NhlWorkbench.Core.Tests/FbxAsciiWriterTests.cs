using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.ModelConversion;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// FbxAsciiWriter is export-only and hand-rolled against the documented FBX 7.x ASCII grammar --
/// there is no Blender/Maya/3ds Max available in this environment to actually import the file
/// into, so these tests verify what CAN be verified here: the output is well-formed (balanced
/// braces, expected sections/node types present) and self-consistent (vertex/polygon counts and
/// values match the source mesh exactly, decoded via the same bitwise-complement end-of-polygon
/// convention the writer uses to encode them). A real DCC-tool import remains the true
/// acceptance test for this feature.
/// </summary>
public class FbxAsciiWriterTests
{
    [Theory]
    [InlineData("rx2_modelassets/feprop_glove_0.rx2")]
    [InlineData("rx2_modelassets/body/hand_2.rx2")]
    [InlineData("rx2_modelassets/jersey/jersey_0_1_2_0_0_2.rx2")]
    public void Write_ProducesWellFormedFbxWithMatchingVertexAndPolygonData(string relativePath)
    {
        if (!SampleFileCatalog.ReferencesAvailable)
        {
            return;
        }

        string path = Path.Combine(SampleFileCatalog.HeckEditorRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(path);
        Rx2Model model = Rx2ModelReader.Parse(data, path).Value!;

        string fbxPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.fbx");
        try
        {
            FbxAsciiWriter.Write(fbxPath, model.Meshes);
            string text = File.ReadAllText(fbxPath);

            int openBraces = text.Count(c => c == '{');
            int closeBraces = text.Count(c => c == '}');
            Assert.True(openBraces > 0);
            Assert.Equal(openBraces, closeBraces);

            Assert.Contains("FBXHeaderExtension:", text);
            Assert.Contains("GlobalSettings:", text);
            Assert.Contains("Objects:", text);
            Assert.Contains("Connections:", text);

            Assert.Equal(model.Meshes.Count, Regex.Matches(text, @"^\tGeometry: \d+, ""Geometry::", RegexOptions.Multiline).Count);
            Assert.Equal(model.Meshes.Count, Regex.Matches(text, @"^\tModel: \d+, ""Model::", RegexOptions.Multiline).Count);
            Assert.Equal(model.Meshes.Count, Regex.Matches(text, @"^\tMaterial: \d+, ""Material::", RegexOptions.Multiline).Count);

            // Every submesh contributes 3 connections: Geometry->Model, Model->root(0), Material->Model.
            int connectionCount = Regex.Matches(text, @"^\tC: ""OO"",", RegexOptions.Multiline).Count;
            Assert.Equal(model.Meshes.Count * 3, connectionCount);

            var vertexBlocks = Regex.Matches(text, @"Vertices: \*(\d+) \{\s*a: ([^\r\n]+)").Cast<Match>().ToArray();
            var polygonBlocks = Regex.Matches(text, @"PolygonVertexIndex: \*(\d+) \{\s*a: ([^\r\n]+)").Cast<Match>().ToArray();
            Assert.Equal(model.Meshes.Count, vertexBlocks.Length);
            Assert.Equal(model.Meshes.Count, polygonBlocks.Length);

            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Rx2Mesh mesh = model.Meshes[i];

                int declaredVertexFloatCount = int.Parse(vertexBlocks[i].Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.Equal(mesh.Vertices.Length * 3, declaredVertexFloatCount);

                float[] writtenFloats = vertexBlocks[i].Groups[2].Value.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                Assert.Equal(declaredVertexFloatCount, writtenFloats.Length);
                for (int v = 0; v < mesh.Vertices.Length; v++)
                {
                    Assert.Equal(mesh.Vertices[v].Position.X, writtenFloats[(v * 3) + 0], 2);
                    Assert.Equal(mesh.Vertices[v].Position.Y, writtenFloats[(v * 3) + 1], 2);
                    Assert.Equal(mesh.Vertices[v].Position.Z, writtenFloats[(v * 3) + 2], 2);
                }

                int declaredPolygonIndexCount = int.Parse(polygonBlocks[i].Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.Equal(mesh.TriangleIndices.Length, declaredPolygonIndexCount);

                int[] writtenIndices = polygonBlocks[i].Groups[2].Value.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                Assert.Equal(declaredPolygonIndexCount, writtenIndices.Length);

                for (int t = 0; t + 2 < writtenIndices.Length; t += 3)
                {
                    int a = writtenIndices[t];
                    int b = writtenIndices[t + 1];
                    int cEncoded = writtenIndices[t + 2];

                    // Only the last index of each polygon is end-of-polygon-marked (bitwise
                    // complemented), so it must decode back to the original non-negative index.
                    Assert.True(cEncoded < 0, "The last index of each polygon must be bitwise-complemented (negative).");
                    int c = ~cEncoded;

                    Assert.Equal(mesh.TriangleIndices[t], a);
                    Assert.Equal(mesh.TriangleIndices[t + 1], b);
                    Assert.Equal(mesh.TriangleIndices[t + 2], c);
                }
            }
        }
        finally
        {
            File.Delete(fbxPath);
        }
    }

    [Fact]
    public void Write_NeverThrows_OverTheFullRealSampleCorpus()
    {
        if (!SampleFileCatalog.ReferencesAvailable)
        {
            return;
        }

        foreach (string path in SampleFileCatalog.AllSampleRx2Files())
        {
            byte[] data = File.ReadAllBytes(path);
            Result<Rx2Model> result = Rx2ModelReader.Parse(data, path);
            if (!result.IsSuccess || result.Value!.Meshes.Count == 0)
            {
                continue;
            }

            string fbxPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.fbx");
            try
            {
                Exception? thrown = Record.Exception(() => FbxAsciiWriter.Write(fbxPath, result.Value.Meshes));
                Assert.Null(thrown);
            }
            finally
            {
                if (File.Exists(fbxPath))
                {
                    File.Delete(fbxPath);
                }
            }
        }
    }
}
