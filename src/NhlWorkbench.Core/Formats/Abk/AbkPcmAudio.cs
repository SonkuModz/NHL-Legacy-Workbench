namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>Decoded, interleaved 16-bit PCM audio ready for playback or WAV export.</summary>
public sealed class AbkPcmAudio
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    /// <summary>Interleaved 16-bit little-endian samples.</summary>
    public required byte[] Pcm16 { get; init; }
}
