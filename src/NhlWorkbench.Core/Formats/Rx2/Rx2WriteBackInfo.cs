namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Format-specific bookkeeping <see cref="Rx2Reader"/> stashes in a
/// <c>TextureEntry.WriteBackMetadata</c> so <see cref="Rx2Writer"/> can locate the exact
/// bytes to patch for an InPlace save, without the public <c>TextureEntry</c> surface
/// needing to know anything about RX2 internals.
/// </summary>
internal sealed class Rx2WriteBackInfo
{
    public required int GpuDataOffsetAbsolute { get; init; }

    public required int GpuDataSize { get; init; }

    public required int DescriptorBase { get; init; }

    /// <summary>Absolute byte offset of this texture's data-block file-table entry (24 bytes) -- needed to patch its offset/size fields when a Rebuild changes this texture's byte size.</summary>
    public required int DataEntryTableOffset { get; init; }

    public required byte OriginalFormatCode { get; init; }
}
