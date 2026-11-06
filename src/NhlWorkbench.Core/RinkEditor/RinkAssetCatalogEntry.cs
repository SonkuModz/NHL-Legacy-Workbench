namespace NhlWorkbench.Core.RinkEditor;

/// <summary>
/// One file in the shared centerlogo/hotspot library under the icesurface folder -- deliberately
/// pixel-free (no decoded texture data) so enumerating the whole library (hundreds of files) stays
/// cheap; thumbnail decoding is the caller's job, done lazily only for entries actually displayed.
/// </summary>
public sealed record RinkAssetCatalogEntry(int Id, string FileName, string FullPath, RinkAssetKind Kind, int? HotspotVariant);
