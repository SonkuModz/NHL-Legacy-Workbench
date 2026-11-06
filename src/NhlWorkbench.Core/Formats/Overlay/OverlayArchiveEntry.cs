namespace NhlWorkbench.Core.Formats.Overlay;

/// <summary>One classified entry from an overlay .big archive -- the browsable row the Scorebug Editor's archive list binds to.</summary>
public sealed record OverlayArchiveEntry(string Name, int Offset, int Size, OverlayEntryKind Kind);
