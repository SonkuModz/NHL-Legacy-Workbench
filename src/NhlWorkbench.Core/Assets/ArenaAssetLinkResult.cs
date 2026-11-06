namespace NhlWorkbench.Core.Assets;

public sealed record ArenaAssetLinkResult(
    IReadOnlyList<ArenaAssetGroup> Arenas,
    IReadOnlyList<SharedIceSurface> SharedIceSurfaces);
