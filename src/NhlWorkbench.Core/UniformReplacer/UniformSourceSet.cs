namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>
/// One scanned source folder for a uniform category. <see cref="SecondaryId"/> is null for
/// categories whose folder name only carries one numeric id (goalie helmet: "goaliehelmet_0_{id}_cm")
/// -- pant and sock both carry two ("_1_{teamId}_{variant}"). <see cref="SlotFiles"/> only contains
/// keys actually found on disk; a partial set is a normal, valid outcome.
/// </summary>
public sealed record UniformSourceSet(
    UniformCategory Category,
    string FolderStem,
    string FolderPath,
    string PrimaryId,
    string? SecondaryId,
    IReadOnlyDictionary<UniformSlotKind, string> SlotFiles);
