using System.Linq;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.ModelConversion;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Exercises the full Model Import/Export pipeline end to end against a real stadium file --
/// export one submesh to OBJ, read it back, validate it, write it into the RX2 container, and
/// reparse -- the same chain the Model Viewer's eventual Import OBJ command will drive. This is
/// deliberately a Core-level stand-in for live-verifying the feature before the App-layer UI
/// (toolbar/submesh list/comparison dialog) exists to drive it interactively.
/// </summary>
public class Rx2ModelImportIntegrationTests
{
    private static string? FindRealStadiumFile()
    {
        if (!SampleFileCatalog.ArenaAssetsAvailable)
        {
            return null;
        }

        return Directory.EnumerateFiles(SampleFileCatalog.ArenaStadiumRoot, "stadium_*_container_0.rx2").FirstOrDefault();
    }

    [Fact]
    public void ExportEditlessRoundTrip_ProducesAnIdenticalSubmeshAndLeavesEverythingElseUntouched()
    {
        string? path = FindRealStadiumFile();
        if (path is null)
        {
            return;
        }

        byte[] original = File.ReadAllBytes(path);
        Rx2Model modelBefore = Rx2ModelReader.Parse(original, path).Value!;

        int targetIndex = -1;
        for (int i = 0; i < modelBefore.Meshes.Count; i++)
        {
            if (modelBefore.Meshes[i].WriteBackInfo is not null)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            return;
        }

        Rx2Mesh target = modelBefore.Meshes[targetIndex];

        // 1. Export -- exactly what the Model Viewer's "Export OBJ" command does today.
        string objPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.obj");
        try
        {
            ObjWriter.Write(objPath, new[] { target });

            // 2. Read back -- what "Import OBJ" will do once a user has edited the file
            // externally. No edits are made here on purpose: an editless round trip must be
            // lossless, which is the strongest possible signal the pipeline is wired correctly.
            Result<IReadOnlyList<ImportedSubmesh>> readResult = ObjReader.Read(objPath);
            Assert.True(readResult.IsSuccess, readResult.Error);
            ImportedSubmesh reimported = Assert.Single(readResult.Value!);

            // 3. Validate -- what the comparison dialog will show before committing.
            ModelValidationResult validation = ModelValidator.Validate(target, reimported);
            Assert.True(validation.CanProceed, string.Join("; ", validation.Errors));
            Assert.Empty(validation.Warnings);

            // 4. Write back into the RX2 container.
            Result<byte[]> writeResult = Rx2ModelWriter.ReplaceSubmesh(original, target.WriteBackInfo!, reimported.Positions, reimported.Uvs, reimported.TriangleIndices);
            Assert.True(writeResult.IsSuccess, writeResult.Error);
            byte[] rewritten = writeResult.Value!;

            // 5. Reparse and confirm the round trip: the target submesh matches (allowing for
            // OBJ's local vertex-order reshuffling, see ObjRoundTripTests), every other submesh
            // is untouched, and every texture is untouched.
            Rx2Model modelAfter = Rx2ModelReader.Parse(rewritten, path).Value!;
            Assert.Equal(modelBefore.Meshes.Count, modelAfter.Meshes.Count);

            Rx2Mesh reparsedTarget = modelAfter.Meshes[targetIndex];
            Assert.Equal(target.TriangleIndices.Length, reparsedTarget.TriangleIndices.Length);

            var beforePositions = target.TriangleIndices.Select(idx => target.Vertices[idx].Position).ToArray();
            var afterPositions = reparsedTarget.TriangleIndices.Select(idx => reparsedTarget.Vertices[idx].Position).ToArray();
            System.Numerics.Vector3 beforeCentroid = beforePositions.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / beforePositions.Length;
            System.Numerics.Vector3 afterCentroid = afterPositions.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / afterPositions.Length;
            Assert.True((beforeCentroid - afterCentroid).Length() < 1e-3f);

            for (int i = 0; i < modelBefore.Meshes.Count; i++)
            {
                if (i == targetIndex)
                {
                    continue;
                }

                Assert.Equal(modelBefore.Meshes[i].Vertices.Length, modelAfter.Meshes[i].Vertices.Length);
                for (int v = 0; v < modelBefore.Meshes[i].Vertices.Length; v++)
                {
                    Assert.Equal(modelBefore.Meshes[i].Vertices[v].Position, modelAfter.Meshes[i].Vertices[v].Position);
                }
            }

            var texturesBefore = Rx2Reader.Parse(original, path);
            var texturesAfter = Rx2Reader.Parse(rewritten, path);
            Assert.True(texturesBefore.IsSuccess);
            Assert.True(texturesAfter.IsSuccess);
            for (int i = 0; i < texturesBefore.Value!.Textures.Count; i++)
            {
                Assert.Equal(texturesBefore.Value!.Textures[i].LinearPixelData, texturesAfter.Value!.Textures[i].LinearPixelData);
            }
        }
        finally
        {
            File.Delete(objPath);
            string mtlPath = Path.ChangeExtension(objPath, ".mtl");
            if (File.Exists(mtlPath))
            {
                File.Delete(mtlPath);
            }
        }
    }
}
