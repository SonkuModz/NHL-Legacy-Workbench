namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>Format-specific bookkeeping <see cref="RpsglReader"/> stashes in a <c>TextureEntry.WriteBackMetadata</c> so <see cref="RpsglWriter"/> can patch the right bytes.</summary>
internal sealed class RpsglWriteBackInfo
{
    public required int GpuDataOffsetAbsolute { get; init; }

    public required int GpuDataSize { get; init; }

    public required int RecordOffset { get; init; }

    public required int DescriptorBase { get; init; }

    public required ushort OriginalFormatCode { get; init; }

    /// <summary>True if this texture's GPU offset was resolved via the special override field rather than the standard cumulative-offset math -- Rebuild always uses the standard layout, so this flags files where that's a meaningful behavior change (see <see cref="RpsglWriter"/> remarks).</summary>
    public required bool UsedGpuStartOverride { get; init; }
}
