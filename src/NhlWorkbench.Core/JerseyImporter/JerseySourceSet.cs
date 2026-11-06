namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>
/// One scanned "texlib_1_{TeamId}_{JerseyVariant}" source folder. <see cref="SlotFiles"/> only
/// contains keys actually found on disk -- a partial set (missing one or more of the 6 files) is a
/// normal, valid outcome, not an error.
/// </summary>
public sealed record JerseySourceSet(
    string TeamId,
    string JerseyVariant,
    string FolderStem,
    string FolderPath,
    IReadOnlyDictionary<JerseySlotKind, string> SlotFiles);
