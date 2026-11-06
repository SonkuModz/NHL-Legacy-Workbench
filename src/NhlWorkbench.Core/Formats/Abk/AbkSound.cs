namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>One sound slot inside an S10A audio bank's per-sound offset table.</summary>
public sealed class AbkSound
{
    /// <summary>Index into the S10A per-sound offset table (the "sample number" ABK_Insert.exe expects).</summary>
    public required int Index { get; init; }

    /// <summary>True if this slot's offset table entry is the dummy marker (0xFFFFFFFF) -- an unused slot.</summary>
    public required bool IsDummy { get; init; }

    public AbkCodecKind Codec { get; init; }

    /// <summary>Raw codec ID nibble, kept even when it doesn't match a known <see cref="AbkCodecKind"/>.</summary>
    public int CodecId { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public int SampleCount { get; init; }

    /// <summary>Absolute file offset of the first byte of encoded audio data.</summary>
    public long PayloadOffset { get; init; }

    /// <summary>Length in bytes of the encoded audio data.</summary>
    public long PayloadLength { get; init; }

    /// <summary>Absolute file offset of this sound's own per-sound header (codec/samplerate/channels word).</summary>
    public long HeaderOffset { get; init; }
}
