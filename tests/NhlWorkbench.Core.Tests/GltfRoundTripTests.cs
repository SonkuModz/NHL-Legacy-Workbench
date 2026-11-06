using System.Linq;
using System.Numerics;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.ModelConversion;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Round-trips GltfWriter's own output back through GltfReader against real sample meshes, mirroring ObjRoundTripTests.</summary>
public class GltfRoundTripTests
{
    [Theory]
    [InlineData("rx2_modelassets/feprop_glove_0.rx2")]
    [InlineData("rx2_modelassets/body/hand_2.rx2")]
    [InlineData("rx2_modelassets/jersey/jersey_0_1_2_0_0_2.rx2")]
    public void WriteThenRead_PreservesSubmeshCountTriangleCountAndGeometricFootprint(string relativePath)
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

        string gltfPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.glb");
        try
        {
            GltfWriter.Write(gltfPath, model.Meshes);

            Result<IReadOnlyList<ImportedSubmesh>> result = GltfReader.Read(gltfPath);
            Assert.True(result.IsSuccess, result.Error);

            IReadOnlyList<ImportedSubmesh> submeshes = result.Value!;
            Assert.Equal(model.Meshes.Count, submeshes.Count);

            for (int i = 0; i < model.Meshes.Count; i++)
            {
                Rx2Mesh original = model.Meshes[i];
                ImportedSubmesh reimported = submeshes[i];

                Assert.Equal(original.TriangleIndices.Length, reimported.TriangleIndices.Length);

                Vector3[] originalPositions = original.TriangleIndices.Select(idx => original.Vertices[idx].Position).ToArray();
                Vector3[] reimportedPositions = reimported.TriangleIndices.Select(idx => reimported.Positions[idx]).ToArray();

                Vector3 originalCentroid = Average(originalPositions);
                Vector3 reimportedCentroid = Average(reimportedPositions);
                Assert.True((originalCentroid - reimportedCentroid).Length() < 1e-3f, $"Centroid drifted: expected {originalCentroid}, got {reimportedCentroid}.");

                (Vector3 originalMin, Vector3 originalMax) = Bounds(originalPositions);
                (Vector3 reimportedMin, Vector3 reimportedMax) = Bounds(reimportedPositions);
                Assert.True((originalMin - reimportedMin).Length() < 1e-3f, $"Bounds min drifted: expected {originalMin}, got {reimportedMin}.");
                Assert.True((originalMax - reimportedMax).Length() < 1e-3f, $"Bounds max drifted: expected {originalMax}, got {reimportedMax}.");
            }
        }
        finally
        {
            File.Delete(gltfPath);
        }
    }

    private static Vector3 Average(Vector3[] points)
    {
        Vector3 sum = Vector3.Zero;
        foreach (Vector3 p in points)
        {
            sum += p;
        }

        return sum / points.Length;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Vector3[] points)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }
}
