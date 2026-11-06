using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// <see cref="Rx2ModelReaderTests.CuratedSamples_MatchGroundTruthVertexAndTriangleCounts"/>
/// hardcodes exact counts independently confirmed by directly running the reference
/// tool's <c>_parse_rx2</c> against these same files (same technique used to catch the
/// magic-byte and RPSGL bugs in Phase 1) -- these are not just plausible numbers, they
/// are the verified ground truth for these specific files.
/// </summary>
public class Rx2ModelReaderTests
{
    public static IEnumerable<object[]> AllSampleFiles()
    {
        IReadOnlyList<string> files = SampleFileCatalog.AllSampleRx2Files();
        if (files.Count == 0)
        {
            yield return new object[] { string.Empty };
            yield break;
        }

        foreach (string file in files)
        {
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Parse_NeverThrows_OverTheFullRealSampleCorpus(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(path);

        Exception? thrown = Record.Exception(() => Rx2ModelReader.Parse(data, path));

        Assert.Null(thrown);
    }

    [Fact]
    public void CuratedSamples_HaveStructurallySaneGeometry()
    {
        IReadOnlyList<string> files = SampleFileCatalog.CuratedRx2Files();
        if (files.Count == 0)
        {
            return;
        }

        int filesWithMeshes = 0;

        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            Result<Rx2Model> result = Rx2ModelReader.Parse(data, path);

            Assert.True(result.IsSuccess, $"{path}: {result.Error}");

            foreach (Rx2Mesh mesh in result.Value!.Meshes)
            {
                filesWithMeshes++;

                Assert.NotEmpty(mesh.Vertices);
                Assert.NotEmpty(mesh.TriangleIndices);
                Assert.Equal(0, mesh.TriangleIndices.Length % 3);

                foreach (int index in mesh.TriangleIndices)
                {
                    Assert.InRange(index, 0, mesh.Vertices.Length - 1);
                }

                foreach (Rx2Vertex vertex in mesh.Vertices)
                {
                    AssertUnitLengthOrZero(vertex.Normal, $"{path}/{mesh.Name}: normal");
                    AssertUnitLengthOrZero(vertex.Tangent, $"{path}/{mesh.Name}: tangent");
                }
            }
        }

        Assert.True(filesWithMeshes > 0, "No curated sample produced any mesh geometry -- this test isn't exercising the mesh path at all.");
    }

    [Theory]
    [InlineData("rx2_modelassets/feprop_glove_0.rx2", 1510, 2343)]
    [InlineData("rx2_modelassets/body/hand_2.rx2", 2266, 3684)]
    [InlineData("rx2_modelassets/jersey/jersey_0_1_2_0_0_2.rx2", 2055, 3894)]
    [InlineData("rx2_modelassets/player/player_1082.rx2", 3941, 7366)]
    public void CuratedSamples_MatchGroundTruthVertexAndTriangleCounts(string relativePath, int expectedVertexCount, int expectedTriangleCount)
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
        Result<Rx2Model> result = Rx2ModelReader.Parse(data, path);

        Assert.True(result.IsSuccess, $"{path}: {result.Error}");

        int totalVertices = result.Value!.Meshes.Sum(m => m.Vertices.Length);
        int totalTriangles = result.Value!.Meshes.Sum(m => m.TriangleIndices.Length / 3);

        Assert.Equal(expectedVertexCount, totalVertices);
        Assert.Equal(expectedTriangleCount, totalTriangles);
    }

    private static void AssertUnitLengthOrZero(Vector3 vector, string label)
    {
        float length = vector.Length();
        bool isZero = length < 1e-6f;
        bool isUnit = Math.Abs(length - 1f) < 1e-3f;
        Assert.True(isZero || isUnit, $"{label}: length {length} is neither ~0 nor ~1.");
    }
}
