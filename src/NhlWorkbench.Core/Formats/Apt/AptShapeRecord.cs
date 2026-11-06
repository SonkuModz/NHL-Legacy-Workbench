namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One of the small, uncompressed numbered entries found between an overlay's movie and its
/// first "sg" marker -- believed to be vector shape/mask geometry (fill styles, edge lists)
/// used by the movie's rendering, based on their size profile (roughly 270-290 bytes per
/// nested child) rather than confirmed field-by-field. Only two fields are actually confirmed,
/// by holding across four differently-sized real entries: the first u32 always equals the
/// entry's own total byte length (self-referential, letting a reader skip the record without
/// understanding it), and the second u32 is a small integer that grows in lockstep with entry
/// size in a way consistent with a child/segment count. Everything past those two fields is
/// exposed only as raw bytes -- there is NOT enough evidence to claim it holds rectangles,
/// coordinates, or any other specific geometry, so no such fields are modeled here.
/// </summary>
public sealed record AptShapeRecord(int DeclaredTotalSize, int ChildCount, byte[] RawData)
{
    /// <summary>True when <see cref="DeclaredTotalSize"/> matches the entry's actual byte length -- the one hard invariant this reader checks before calling something a shape record at all.</summary>
    public bool SelfLengthConfirmed => DeclaredTotalSize == RawData.Length;
}
