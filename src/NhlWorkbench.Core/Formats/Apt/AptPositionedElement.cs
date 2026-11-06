namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One real, on-screen (per the depth-replay final display list) root-level element, positioned
/// and sized for a "components in place" preview. See <see cref="AptPositionedPreviewBuilder"/>
/// for how this is derived and its known accuracy limits.
/// </summary>
public sealed record AptPositionedElement(
    string Name,
    uint CharacterId,
    float X,
    float Y,
    float Width,
    float Height,
    AptPositionedElementKind Kind,
    string? TextureEntryName,
    bool RespondsToMove);
