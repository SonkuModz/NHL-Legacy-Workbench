using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>One replacement submesh's outcome from a bulk apply.</summary>
public sealed record SubmeshImportOutcome(string SubmeshName, bool Applied, IReadOnlyList<string> Warnings, string? SkipReason);

/// <summary>Result of applying every matched replacement in a bulk import, in order.</summary>
public sealed record BulkImportResult(byte[] UpdatedData, int AppliedCount, int TotalCount, IReadOnlyList<SubmeshImportOutcome> Outcomes);

/// <summary>
/// Matches a whole set of replacement submeshes (e.g. every "o" group in one exported-then-
/// edited OBJ) against a target <see cref="Rx2Model"/>, and applies every match in sequence.
/// Two matching strategies, for two different trust levels:
///
/// <see cref="ApplyByPosition"/> matches purely by index (replacement i -> target submesh i) and
/// requires the counts to match exactly. This is safe specifically for the "export this file,
/// edit it, reimport it" round trip in the same app session -- <see cref="ObjWriter"/> and <see
/// cref="GltfWriter"/> always write submeshes in <c>model.Meshes</c> order, so as long as the
/// external edit didn't add/remove/reorder objects, position is a completely unambiguous match
/// even when many submeshes share the same name (stadium/arena containers routinely do, e.g.
/// dozens of submeshes all named "boardconcreteambient").
///
/// <see cref="ApplyByName"/> matches by name, and only when a name is unique within the target
/// file -- used when there's no positional guarantee to lean on (e.g. Batch Tools' folder-wide
/// import, where the edited file didn't necessarily come from this exact export). An ambiguous
/// or missing name is skipped (recorded in the outcome, never applied) rather than guessed at.
///
/// Both are shared by the Batch Tools "Batch Model Import" command and the Model Viewer's
/// "replace the whole file" mode (Import OBJ/glTF with no submesh selected), which prefers
/// <see cref="ApplyByPosition"/> when the counts line up and falls back to <see
/// cref="ApplyByName"/> otherwise.
///
/// Applying matches sequentially against an evolving byte buffer is safe either way: <see
/// cref="Rx2ModelWriter.ReplaceSubmesh"/> always re-resolves each buffer's current file-table
/// offsets fresh from whatever bytes it's given, and one submesh's write appending past the
/// current end of file never invalidates another submesh's already-computed header-region
/// offsets (those live well before the original end of file).
/// </summary>
public static class ModelBulkImporter
{
    public static BulkImportResult ApplyByPosition(byte[] originalData, Rx2Model model, IReadOnlyList<ImportedSubmesh> replacements)
    {
        if (replacements.Count != model.Meshes.Count)
        {
            throw new ArgumentException($"Positional matching requires equal counts (target has {model.Meshes.Count} submesh(es), replacements has {replacements.Count}).", nameof(replacements));
        }

        byte[] currentBytes = originalData;
        int appliedCount = 0;
        var outcomes = new List<SubmeshImportOutcome>();

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            (currentBytes, SubmeshImportOutcome outcome) = ApplyOne(currentBytes, model.Meshes[i], replacements[i], model.Meshes[i].Name);
            outcomes.Add(outcome);
            if (outcome.Applied)
            {
                appliedCount++;
            }
        }

        return new BulkImportResult(currentBytes, appliedCount, replacements.Count, outcomes);
    }

    public static BulkImportResult ApplyByName(byte[] originalData, Rx2Model model, IReadOnlyList<ImportedSubmesh> replacements)
    {
        Dictionary<string, int?> targetIndexByName = model.Meshes
            .Select((mesh, index) => (mesh.Name, index))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single().index : (int?)null);

        byte[] currentBytes = originalData;
        int appliedCount = 0;
        var outcomes = new List<SubmeshImportOutcome>();

        foreach (ImportedSubmesh replacement in replacements)
        {
            if (!targetIndexByName.TryGetValue(replacement.Name, out int? targetIndex) || targetIndex is null)
            {
                outcomes.Add(new SubmeshImportOutcome(replacement.Name, false, Array.Empty<string>(), "name not found or ambiguous in the target file"));
                continue;
            }

            SubmeshImportOutcome outcome;
            (currentBytes, outcome) = ApplyOne(currentBytes, model.Meshes[targetIndex.Value], replacement, replacement.Name);
            outcomes.Add(outcome);
            if (outcome.Applied)
            {
                appliedCount++;
            }
        }

        return new BulkImportResult(currentBytes, appliedCount, replacements.Count, outcomes);
    }

    private static (byte[] UpdatedBytes, SubmeshImportOutcome Outcome) ApplyOne(byte[] currentBytes, Rx2Mesh target, ImportedSubmesh replacement, string outcomeName)
    {
        if (target.WriteBackInfo is not Rx2MeshWriteBackInfo writeBackInfo)
        {
            return (currentBytes, new SubmeshImportOutcome(outcomeName, false, Array.Empty<string>(), "submesh doesn't support write-back"));
        }

        // The bulk paths are non-interactive, so the origin-mismatch rescue the Model Viewer
        // offers as a dialog is applied automatically here (and recorded in the outcome's
        // warnings). Without this, a bulk import of a model authored around the DCC origin
        // silently lands far from its submesh -- observed live: a correctly placed screen
        // was destroyed seconds later by re-importing the same OBJ through this path.
        // Translation only, never scaling; round-tripped exports re-import in place, so this
        // never triggers for them.
        List<string>? extraWarnings = null;
        if (ModelAutoFit.LooksMisplaced(target, replacement))
        {
            replacement = ModelAutoFit.FitToOriginal(target, replacement);
            extraWarnings = new List<string> { "auto-centered onto the original submesh's position (imported model was far away; size not changed)" };
        }

        ModelValidationResult validation = ModelValidator.Validate(target, replacement);
        if (!validation.CanProceed)
        {
            return (currentBytes, new SubmeshImportOutcome(outcomeName, false, Array.Empty<string>(), string.Join("; ", validation.Errors)));
        }

        Result<byte[]> writeResult = Rx2ModelWriter.ReplaceSubmesh(currentBytes, writeBackInfo, replacement.Positions, replacement.Uvs, replacement.TriangleIndices);
        if (!writeResult.IsSuccess)
        {
            return (currentBytes, new SubmeshImportOutcome(outcomeName, false, Array.Empty<string>(), writeResult.Error));
        }

        IReadOnlyList<string> warnings = extraWarnings is null
            ? validation.Warnings
            : extraWarnings.Concat(validation.Warnings).ToList();
        return (writeResult.Value!, new SubmeshImportOutcome(outcomeName, true, warnings, null));
    }
}
