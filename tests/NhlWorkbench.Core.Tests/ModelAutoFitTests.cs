using System.Linq;
using System.Numerics;
using NhlWorkbench.Core.Formats.Rx2;
using NhlWorkbench.Core.ModelConversion;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// <see cref="ModelAutoFit"/> exists for the exact failure observed live: a jumbotron-screen
/// replacement authored around the DCC origin imported "successfully" but landed far from the
/// jumbotron, reading as "the import deleted my model". The fix is translation-ONLY by
/// explicit user decision -- the model's size must never be altered, so these tests pin both
/// the trigger heuristic and the size-preservation guarantee.
/// </summary>
public class ModelAutoFitTests
{
    private static Rx2Mesh MakeOriginal(Vector3 center, float halfExtent)
    {
        var vertices = new Rx2Vertex[8];
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                ((i & 1) == 0 ? -1 : 1) * halfExtent,
                ((i & 2) == 0 ? -1 : 1) * halfExtent,
                ((i & 4) == 0 ? -1 : 1) * halfExtent);
            vertices[i] = new Rx2Vertex(center + corner, Vector3.UnitY, Vector3.UnitX, Vector2.Zero);
        }

        return new Rx2Mesh { Name = "original", Vertices = vertices, TriangleIndices = new[] { 0, 1, 2 } };
    }

    private static ImportedSubmesh MakeReplacement(Vector3 center, float halfExtent)
    {
        var positions = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            positions[i] = center + new Vector3(
                ((i & 1) == 0 ? -1 : 1) * halfExtent,
                ((i & 2) == 0 ? -1 : 1) * halfExtent,
                ((i & 4) == 0 ? -1 : 1) * halfExtent);
        }

        var uvs = Enumerable.Range(0, 8).Select(i => new Vector2(i / 8f, 1f - (i / 8f))).ToArray();
        return new ImportedSubmesh("replacement", positions, uvs, new[] { 0, 1, 2, 2, 1, 3 });
    }

    [Fact]
    public void LooksMisplaced_ModelAtOriginFarFromTarget_IsFlagged()
    {
        // The live case: screen at y~1600, replacement authored around the scene origin.
        Rx2Mesh original = MakeOriginal(new Vector3(0, 1600, 0), 450f);
        ImportedSubmesh replacement = MakeReplacement(Vector3.Zero, 18f);

        Assert.True(ModelAutoFit.LooksMisplaced(original, replacement));
    }

    [Fact]
    public void LooksMisplaced_WrongSizeButInPlace_IsNotFlagged()
    {
        // Size mismatches are the author's business (and ModelValidator's warning) --
        // centering can't help, so it must not trigger.
        Rx2Mesh original = MakeOriginal(new Vector3(0, 1600, 0), 450f);
        ImportedSubmesh replacement = MakeReplacement(new Vector3(0, 1600, 0), 18f);

        Assert.False(ModelAutoFit.LooksMisplaced(original, replacement));
    }

    [Fact]
    public void LooksMisplaced_CloseMatch_IsNotFlagged()
    {
        Rx2Mesh original = MakeOriginal(new Vector3(0, 1600, 0), 450f);
        ImportedSubmesh replacement = MakeReplacement(new Vector3(30, 1550, -20), 380f);

        Assert.False(ModelAutoFit.LooksMisplaced(original, replacement));
    }

    [Fact]
    public void FitToOriginal_RecentersOnly_SizeUvsAndTrianglesUntouched()
    {
        Rx2Mesh original = MakeOriginal(new Vector3(0, 1600, 0), 450f);
        ImportedSubmesh replacement = MakeReplacement(new Vector3(3, -2, 7), 18f);

        ImportedSubmesh fitted = ModelAutoFit.FitToOriginal(original, replacement);

        Vector3 min = fitted.Positions.Aggregate(Vector3.Min);
        Vector3 max = fitted.Positions.Aggregate(Vector3.Max);
        Vector3 center = (min + max) * 0.5f;
        Assert.Equal(0f, center.X, 2);
        Assert.Equal(1600f, center.Y, 2);
        Assert.Equal(0f, center.Z, 2);

        // Size preserved exactly: still a 36-unit cube, NOT scaled up to the original's 900.
        Assert.Equal(36f, max.X - min.X, 2);
        Assert.Equal(36f, max.Y - min.Y, 2);
        Assert.Equal(36f, max.Z - min.Z, 2);

        Assert.Equal(replacement.Uvs, fitted.Uvs);
        Assert.Equal(replacement.TriangleIndices, fitted.TriangleIndices);
        Assert.False(ModelAutoFit.LooksMisplaced(original, fitted));
    }

    [Fact]
    public void FitToOriginal_PreservesRelativeVertexLayout()
    {
        Rx2Mesh original = MakeOriginal(new Vector3(0, 1600, 0), 450f);
        ImportedSubmesh replacement = MakeReplacement(new Vector3(3, -2, 7), 18f);

        ImportedSubmesh fitted = ModelAutoFit.FitToOriginal(original, replacement);

        // Pure translation: every vertex moved by the same offset.
        Vector3 offset = fitted.Positions[0] - replacement.Positions[0];
        for (int i = 1; i < fitted.Positions.Length; i++)
        {
            Vector3 delta = fitted.Positions[i] - replacement.Positions[i];
            Assert.Equal(offset.X, delta.X, 3);
            Assert.Equal(offset.Y, delta.Y, 3);
            Assert.Equal(offset.Z, delta.Z, 3);
        }
    }
}
