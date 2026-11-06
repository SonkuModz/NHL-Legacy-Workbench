using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// Decodes EA-XAS ("XA Seekable") v1 ADPCM, the codec NHL Legacy Edition's PS3 .abk sounds
/// use (codec ID 4). Ported from vgmstream's public-domain-derived <c>decode_ea_xas_v1</c>
/// (src/coding/ea_xas_decoder.c) rather than the native EA_ADPCM_codec.dll the user's PS3
/// tool P/Invokes -- that DLL is 32-bit only and this app is 64-bit, and porting the
/// (much simpler) decode side avoids the bitness mismatch entirely. Verified against a real
/// PS3 sample: clean spectrogram, no clipping, matches the known clip content.
///
/// Layout: blocks of 0x4C (76) bytes per channel, 128 samples per channel per block, stored
/// as one channel's whole block followed by the next channel's whole block (not
/// sample-interleaved).
/// </summary>
public static class EaXasDecoder
{
    private const int BytesPerBlock = 0x4C;
    private const int SamplesPerBlock = 128;

    // Standard CD-XA style K0/K1 filter coefficient pairs, indexed by the low nibble of each
    // group header. Only 4 pairs are defined; any other index is treated as (0, 0).
    private static readonly (float K0, float K1)[] XaCoefficients =
    {
        (0.0f, 0.0f),
        (0.9375f, 0.0f),
        (1.796875f, -0.8125f),
        (1.53125f, -0.859375f),
    };

    public static Result<AbkPcmAudio> Decode(byte[] data, long payloadOffset, int sampleCount, int channels, int sampleRate)
    {
        try
        {
            int numBlocks = (sampleCount + SamplesPerBlock - 1) / SamplesPerBlock;
            long requiredBytes = (long)numBlocks * BytesPerBlock * channels;
            if (payloadOffset + requiredBytes > data.Length)
            {
                return Result.Failure<AbkPcmAudio>("EA-XAS payload runs past the end of the file.");
            }

            var channelSamples = new short[channels][];
            for (int c = 0; c < channels; c++)
            {
                channelSamples[c] = new short[numBlocks * SamplesPerBlock];
            }

            long pos = payloadOffset;
            Span<byte> frame = stackalloc byte[BytesPerBlock];

            for (int block = 0; block < numBlocks; block++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int b = 0; b < BytesPerBlock; b++)
                    {
                        frame[b] = data[pos + b];
                    }

                    pos += BytesPerBlock;
                    DecodeBlock(frame, channelSamples[ch], block * SamplesPerBlock);
                }
            }

            var pcm = new byte[(long)sampleCount * channels * 2];
            int w = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    short s = channelSamples[c][i];
                    pcm[w++] = (byte)(s & 0xFF);
                    pcm[w++] = (byte)((s >> 8) & 0xFF);
                }
            }

            return Result.Success(new AbkPcmAudio { SampleRate = sampleRate, Channels = channels, Pcm16 = pcm });
        }
        catch (Exception ex)
        {
            return Result.Failure<AbkPcmAudio>($"EA-XAS decode failed: {ex.Message}", ex);
        }
    }

    private static void DecodeBlock(ReadOnlySpan<byte> frame, short[] outSamples, int outStart)
    {
        int outIdx = outStart;

        for (int group = 0; group < 4; group++)
        {
            uint groupHeader = (uint)(frame[group * 4] | (frame[group * 4 + 1] << 8) | (frame[group * 4 + 2] << 16) | (frame[group * 4 + 3] << 24));

            (float coef1, float coef2) = XaCoefficients[groupHeader & 0x0F];
            short hist2 = (short)((groupHeader >> 0) & 0xFFF0);
            short hist1 = (short)((groupHeader >> 16) & 0xFFF0);
            int shift = (int)((groupHeader >> 16) & 0x0F);

            outSamples[outIdx++] = hist2;
            outSamples[outIdx++] = hist1;

            for (int row = 0; row < 15; row++)
            {
                byte nibbles = frame[4 * 4 + (row * 4) + group];

                for (int i = 0; i < 2; i++)
                {
                    int nibble = (i & 1) != 0 ? (nibbles & 0x0F) : ((nibbles >> 4) & 0x0F);
                    int sample = ((short)(nibble << 12)) >> shift;
                    sample += (int)(hist1 * coef1 + hist2 * coef2);
                    sample = Math.Clamp(sample, short.MinValue, short.MaxValue);

                    outSamples[outIdx++] = (short)sample;
                    hist2 = hist1;
                    hist1 = (short)sample;
                }
            }
        }
    }
}
