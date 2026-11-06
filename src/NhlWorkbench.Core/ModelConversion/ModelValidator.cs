using System.Numerics;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Validates a replacement submesh against the original RX2 submesh it would overwrite.
/// Only the 16-bit index-buffer vertex limit and an empty replacement are hard errors; vertex/
/// triangle-count deltas and large bounding-box scale changes are warnings the caller may let
/// the user proceed past, matching the feature's explicit warn-but-allow-override requirement.
/// </summary>
public static class ModelValidator
{
    /// <summary>Bounding-box longest-dimension ratios outside this range (either direction) are flagged -- generous enough that intentional resizing doesn't spam warnings, tight enough to catch a likely unit-scale mismatch (e.g. importing a model authored in meters into a scene modeled in centimeters).</summary>
    private const float ScaleWarningRatio = 3f;

    public static ModelValidationResult Validate(Rx2Mesh original, ImportedSubmesh replacement)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (replacement.Positions.Length == 0)
        {
            errors.Add("Replacement mesh has zero vertices.");
        }

        if (replacement.Positions.Length > Rx2ModelWriter.MaxVertexCount)
        {
            errors.Add($"Replacement has {replacement.Positions.Length} vertices, which exceeds the RX2 hard limit of {Rx2ModelWriter.MaxVertexCount} (16-bit index buffer).");
        }

        int originalTriangleCount = original.TriangleIndices.Length / 3;
        int newTriangleCount = replacement.TriangleIndices.Length / 3;

        if (replacement.Positions.Length != original.Vertices.Length)
        {
            warnings.Add($"Vertex count changed: {original.Vertices.Length} -> {replacement.Positions.Length}.");
        }

        if (newTriangleCount != originalTriangleCount)
        {
            warnings.Add($"Triangle count changed: {originalTriangleCount} -> {newTriangleCount}.");
        }

        (Vector3 originalMin, Vector3 originalMax) = ComputeBounds(original.Vertices.Select(v => v.Position));
        (Vector3 newMin, Vector3 newMax) = ComputeBounds(replacement.Positions);

        float originalMaxDimension = MaxDimension(originalMax - originalMin);
        float newMaxDimension = MaxDimension(newMax - newMin);

        if (originalMaxDimension > 1e-6f && newMaxDimension > 1e-6f)
        {
            float ratio = newMaxDimension / originalMaxDimension;
            if (ratio > ScaleWarningRatio || ratio < 1f / ScaleWarningRatio)
            {
                warnings.Add($"Bounding-box size changed by {ratio:0.00}x relative to the original -- double-check units/scale if this wasn't intentional.");
            }
        }

        return new ModelValidationResult
        {
            CanProceed = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            OriginalVertexCount = original.Vertices.Length,
            NewVertexCount = replacement.Positions.Length,
            OriginalTriangleCount = originalTriangleCount,
            NewTriangleCount = newTriangleCount,
            OriginalBoundsMin = originalMin,
            OriginalBoundsMax = originalMax,
            NewBoundsMin = newMin,
            NewBoundsMax = newMax,
        };
    }

    private static float MaxDimension(Vector3 size) => Math.Max(size.X, Math.Max(size.Y, size.Z));

    private static (Vector3 Min, Vector3 Max) ComputeBounds(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;

        foreach (Vector3 point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
            any = true;
        }

        return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }
}
