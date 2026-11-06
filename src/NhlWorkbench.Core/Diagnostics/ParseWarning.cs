namespace NhlWorkbench.Core.Diagnostics;

/// <summary>
/// A non-fatal issue encountered while parsing (e.g. a missing name table forced
/// synthetic texture names). Distinct from a hard failure: the operation still
/// produced a usable result.
/// </summary>
public sealed record ParseWarning(string Message, string? Source = null);
