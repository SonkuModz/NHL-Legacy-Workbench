namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>Trims a sound, changes how loud it is, and fades its ends. Works on plain 16-bit samples.</summary>
/// <param name="StartSeconds">How much to cut off the front.</param>
/// <param name="LengthSeconds">How much to keep from there. Null keeps the rest.</param>
/// <param name="VolumeDb">Louder above 0, quieter below. Anything that would clip is held at the limit.</param>
public sealed record SoundShape(double StartSeconds = 0, double? LengthSeconds = null, double VolumeDb = 0, double FadeInSeconds = 0, double FadeOutSeconds = 0)
{
    public bool DoesNothing => StartSeconds <= 0 && LengthSeconds is null && VolumeDb == 0 && FadeInSeconds <= 0 && FadeOutSeconds <= 0;

    /// <summary>Returns the shaped samples. What goes in isn't changed.</summary>
    public byte[] Apply(byte[] pcm16, int sampleRate, int channels)
    {
        int frameBytes = 2 * channels;
        int frames = pcm16.Length / frameBytes;
        int first = Math.Clamp((int)(Math.Max(StartSeconds, 0) * sampleRate), 0, frames);
        int kept = LengthSeconds is { } length ? Math.Clamp((int)(Math.Max(length, 0) * sampleRate), 0, frames - first) : frames - first;
        var shaped = new byte[kept * frameBytes];
        Array.Copy(pcm16, first * frameBytes, shaped, 0, shaped.Length);

        double gain = Math.Pow(10, VolumeDb / 20);
        int fadeIn = Math.Min((int)(Math.Max(FadeInSeconds, 0) * sampleRate), kept);
        int fadeOut = Math.Min((int)(Math.Max(FadeOutSeconds, 0) * sampleRate), kept);
        if (gain == 1 && fadeIn == 0 && fadeOut == 0)
        {
            return shaped;
        }

        for (int frame = 0; frame < kept; frame++)
        {
            double level = gain;
            if (frame < fadeIn)
            {
                level *= (double)frame / fadeIn;
            }

            if (frame >= kept - fadeOut)
            {
                level *= (double)(kept - 1 - frame) / Math.Max(fadeOut - 1, 1);
            }

            for (int channel = 0; channel < channels; channel++)
            {
                int at = ((frame * channels) + channel) * 2;
                var sample = (short)(shaped[at] | (shaped[at + 1] << 8));
                int scaled = (int)Math.Clamp(Math.Round(sample * level), short.MinValue, short.MaxValue);
                shaped[at] = (byte)scaled;
                shaped[at + 1] = (byte)(scaled >> 8);
            }
        }

        return shaped;
    }
}
