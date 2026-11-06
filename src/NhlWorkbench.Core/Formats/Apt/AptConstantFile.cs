namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// A parsed "Apt constant file" -- the uncompressed-or-RefPack-compressed entry
/// (conventionally the archive's last real entry) that holds the string/value
/// constant pool the movie's bytecode operands index into, plus the header fields
/// that tell a reader exactly where the paired movie's tag stream begins.
///
/// Header layout confirmed 2026-08-19 against 3 real NHL overlay archives (bytes
/// re-verified independently of, but consistent in shape with, the external
/// community research in References/aptresearch/NCAA14_APT_SPEC.md's "CONST File"
/// section -- NHL's variant uses the literal readable string "Apt constant file" as
/// its signature rather than NCAA's 4-byte "Apt1" magic, but the same tag-byte +
/// align-to-4 + fixed-offset-header shape the movie header itself uses):
/// "Apt constant file" (printable run) + 1 tag byte (0x1A, same value the movie
/// header uses) + padding to the next 4-byte boundary = 0x14 for every real sample
/// (all three real CONST files' printable signature is short enough that this
/// lands the fixed fields at the same absolute offsets every time). <see
/// cref="AptDataOffset"/> at 0x14 exactly matches the paired movie's own tag-stream
/// start offset in all 3 samples (828/452/156 -- independently re-derived from the
/// movie's own bytes by <see cref="AptMovieReader"/>'s string-pool-end heuristic,
/// so this is a cross-validated fact, not a single-source guess). <see
/// cref="ItemCount"/> at 0x18, <see cref="TablePointer"/> at 0x1C (always 0x20 in
/// every real sample -- immediately after this 32-byte header).
/// </summary>
public sealed class AptConstantFile
{
    public required uint AptDataOffset { get; init; }

    public required uint ItemCount { get; init; }

    public required uint TablePointer { get; init; }

    public required IReadOnlyList<AptConstantItem> Items { get; init; }

    /// <summary>Every null-terminated ASCII string (3+ chars) found anywhere in the file -- a flat safety-net scan, independent of the structured <see cref="Items"/> table.</summary>
    public required IReadOnlyList<(int Offset, string Value)> AllStrings { get; init; }

    public required byte[] RawData { get; init; }
}
