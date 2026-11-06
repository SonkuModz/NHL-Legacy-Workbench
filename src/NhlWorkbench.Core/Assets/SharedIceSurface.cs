namespace NhlWorkbench.Core.Assets;

/// <summary>One of the small set of ice-surface texture containers shared by every arena (NHL rules,
/// international rules, and a full-lightmap variant) rather than being numbered per-arena like
/// stadium/logo/jumbotron files are.</summary>
public sealed record SharedIceSurface(string Name, string Path);
