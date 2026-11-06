namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>A source set paired with its matching destination RX2 path, if any was found. A null
/// <see cref="DestinationPath"/> means no ".rx2" file with a matching stem exists at the destination
/// category root -- a normal, expected outcome (e.g. only 64 of the 78 goalie helmet source sets
/// have a real destination counterpart, confirmed against the real tree).</summary>
public sealed record UniformMatchResult(UniformSourceSet Source, string? DestinationPath);
