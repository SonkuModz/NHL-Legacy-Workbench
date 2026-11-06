using System.Numerics;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Detects and repairs the classic "imported the model at a different origin" mistake: a
/// replacement mesh authored around the DCC scene origin, imported into a game submesh that
/// lives hundreds of game-units away. The write itself succeeds, but in the arena the new
/// model sits somewhere nobody is looking -- which reads as "the import deleted my model".
/// <see cref="LooksMisplaced"/> is the trigger the UI uses to offer the fix;
/// <see cref="FitToOriginal"/> translates the replacement so its bounding-box center lands on
/// the original submesh's center. Deliberately translation-ONLY: the model's size is the
/// author's decision (an earlier uniform-rescale variant was rejected by the user), so scale
/// mismatches are left to the <see cref="ModelValidator"/> bounding-box warning instead.
/// </summary>
public static class ModelAutoFit
{
    /// <summary>Center offsets beyond this fraction of the original's largest dimension count as misplaced.</summary>
    public const float OffsetTriggerFraction = 0.75f;

    public static bool LooksMisplaced(Rx2Mesh original, ImportedSubmesh replacement)
    {
        if (original.Vertices.Length == 0 || replacement.Positions.Length == 0)
        {
            return false;
        }

        (Vector3 originalMin, Vector3 originalMax) = Bounds(original.Vertices.Select(v => v.Position));
        (Vector3 newMin, Vector3 newMax) = Bounds(replacement.Positions);

        float originalMaxDimension = MaxDimension(originalMax - originalMin);
        if (originalMaxDimension <= 1e-6f)
        {
            return false;
        }

        Vector3 centerOffset = ((newMin + newMax) * 0.5f) - ((originalMin + originalMax) * 0.5f);
        return centerOffset.Length() > originalMaxDimension * OffsetTriggerFraction;
    }

    public static ImportedSubmesh FitToOriginal(Rx2Mesh original, ImportedSubmesh replacement)
    {
        if (original.Vertices.Length == 0 || replacement.Positions.Length == 0)
        {
            return replacement;
        }

        (Vector3 originalMin, Vector3 originalMax) = Bounds(original.Vertices.Select(v => v.Position));
        (Vector3 newMin, Vector3 newMax) = Bounds(replacement.Positions);

        Vector3 offset = ((originalMin + originalMax) * 0.5f) - ((newMin + newMax) * 0.5f);

        var moved = new Vector3[replacement.Positions.Length];
        for (int i = 0; i < moved.Length; i++)
        {
            moved[i] = replacement.Positions[i] + offset;
        }

        return replacement with { Positions = moved };
    }

    private static float MaxDimension(Vector3 size) => Math.Max(size.X, Math.Max(size.Y, size.Z));

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }
}
