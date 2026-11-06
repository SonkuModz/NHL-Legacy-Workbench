namespace NhlWorkbench.Core.Containers;

public enum PatchStrategy
{
    /// <summary>
    /// Replace only the GPU pixel bytes in place; the header, descriptors, and name
    /// table are byte-for-byte unchanged. Only valid when every staged replacement
    /// matches its original texture's format, dimensions, and mip count exactly.
    /// </summary>
    InPlace,

    /// <summary>
    /// Recompute the entire header/descriptor/name-table/file-table layout from
    /// scratch. Always valid; used whenever any staged replacement differs in format,
    /// dimensions, or mip count from the original (Phase 1 does not implement a
    /// narrower size-changed patch — Rebuild is its safe superset).
    /// </summary>
    Rebuild,
}

[Flags]
public enum PatchCapabilities
{
    None = 0,
    InPlace = 1 << 0,
    Rebuild = 1 << 1,
}
