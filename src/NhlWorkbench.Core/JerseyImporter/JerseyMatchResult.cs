namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>A source set paired with its matching destination RX2 path, if any was found. A null
/// <see cref="DestinationPath"/> means no ".rx2" file with a matching stem exists at the destination
/// root -- a normal, expected outcome for a source set with no corresponding compiled container.</summary>
public sealed record JerseyMatchResult(JerseySourceSet Source, string? DestinationPath);
