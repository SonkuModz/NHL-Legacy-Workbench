using System.Numerics;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>
/// Compares a replacement submesh against the original it would replace. <see cref="CanProceed"/>
/// is false only for hard-blocking issues (e.g. exceeding the 16-bit index-buffer vertex limit);
/// everything else is a warning the caller may let the user override, per the explicit
/// warn-but-allow behavior the Model Import/Export feature calls for.
/// </summary>
public sealed class ModelValidationResult
{
    public required bool CanProceed { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required int OriginalVertexCount { get; init; }

    public required int NewVertexCount { get; init; }

    public required int OriginalTriangleCount { get; init; }

    public required int NewTriangleCount { get; init; }

    public required Vector3 OriginalBoundsMin { get; init; }

    public required Vector3 OriginalBoundsMax { get; init; }

    public required Vector3 NewBoundsMin { get; init; }

    public required Vector3 NewBoundsMax { get; init; }
}
