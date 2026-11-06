using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>Dispatches sound decoding to the right codec-specific decoder.</summary>
public static class AbkAudioDecoder
{
    public static Result<AbkPcmAudio> Decode(AbkFile file, AbkSound sound, string? ffmpegExePath)
    {
        if (sound.IsDummy)
        {
            return Result.Failure<AbkPcmAudio>("This slot is empty (dummy sound).");
        }

        return sound.Codec switch
        {
            AbkCodecKind.EaXas => EaXasDecoder.Decode(file.Data, sound.PayloadOffset, sound.SampleCount, sound.Channels, sound.SampleRate),
            AbkCodecKind.Xma2 => ffmpegExePath is null
                ? Result.Failure<AbkPcmAudio>("Decoding Xbox 360 XMA2 audio requires ffmpeg.exe, which was not found.")
                : Xma2Decoder.Decode(file.Data, sound.PayloadOffset, sound.PayloadLength, sound.SampleCount, sound.SampleRate, sound.Channels, ffmpegExePath),
            _ => Result.Failure<AbkPcmAudio>($"Codec ID {sound.CodecId} is not supported for preview yet."),
        };
    }
}
