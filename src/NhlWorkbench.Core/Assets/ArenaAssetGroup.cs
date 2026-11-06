namespace NhlWorkbench.Core.Assets;

/// <summary>Every texture asset that belongs to one NHL Legacy arena, linked by the shared numeric
/// arena ID embedded in each file's name. Only <see cref="StadiumContainerPath"/> is guaranteed --
/// center-ice logos and jumbotron texlibs don't map 1:1 to every stadium, so those are optional
/// and a missing link is normal, not an error.</summary>
public sealed record ArenaAssetGroup(
    int ArenaId,
    string StadiumContainerPath,
    string? CenterLogoPath,
    string? JumbotronPath);
