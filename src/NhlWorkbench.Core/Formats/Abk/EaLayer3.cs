using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// One granule of an EALayer3 v1 stream: what MPEG Layer III calls a granule, split out into its own little frame so a
/// stream can be cut anywhere. The bits inside are ordinary Layer III, only the wrapping differs.
/// </summary>
public sealed class EaGranule
{
    public required int Version { get; init; }

    public required int RateIndex { get; init; }

    public required int ChannelMode { get; init; }

    public required int ModeExtension { get; init; }

    /// <summary>Which of its MPEG frame's two granules this is. Always 0 for MPEG-2.</summary>
    public required int Index { get; init; }

    /// <summary>Per channel, for the second granule of an MPEG-1 frame.</summary>
    public required int[] Scfsi { get; init; }

    public required int[] MainBitLengths { get; init; }

    /// <summary>Per channel, the rest of the side info: 47 bits for MPEG-1 and 51 for MPEG-2.</summary>
    public required ulong[] SideBits { get; init; }

    /// <summary>The Layer III main data, starting at bit 0.</summary>
    public required byte[] Main { get; init; }

    /// <summary>The raw samples that open a sound, planar, carried by one early granule.</summary>
    public short[]? Pcm { get; set; }

    public int PcmSamples { get; set; }

    public bool IsMpeg1 => Version == 3;

    public int Channels => ChannelMode == 3 ? 1 : 2;

    public int SampleRate => (IsMpeg1 ? MpegRates1 : MpegRates2)[RateIndex];

    internal static readonly int[] MpegRates1 = [44100, 48000, 32000];

    internal static readonly int[] MpegRates2 = [22050, 24000, 16000];
}

/// <summary>Reads, writes and converts EALayer3 v1 (codec 5), the codec of every sound on the PS3 disc.</summary>
public static class EaLayer3
{
    /// <summary>How many samples a decoder hands out before a stream's first real one.</summary>
    public const int WarmUpSamples = 1152;

    /// <summary>The raw samples an ordinary stream opens with, which together with the warm-up make up the coder's delay.</summary>
    public const int LeadInSamples = 47;

    private const int FlagPlain = 0x00;
    private const int FlagPcm = 0xEE;

    /// <summary>Walks a run of frames. Zero padding after the last one is ignored.</summary>
    public static Result<List<EaGranule>> Read(byte[] data, int offset, int length)
    {
        var frames = new List<EaGranule>();
        int position = offset;
        int end = offset + length;
        while (position < end)
        {
            if (end - position < 2)
            {
                if (data[position] == 0)
                {
                    break;
                }

                return Result.Failure<List<EaGranule>>("The audio ends part way through a frame.");
            }

            int flag = data[position];
            int common = data[position + 1];
            if (flag == FlagPlain && common == 0)
            {
                bool padding = true;
                for (int i = position; i < end && padding; i++)
                {
                    padding = data[i] == 0;
                }

                if (padding)
                {
                    break;
                }
            }

            if (flag != FlagPlain && flag != FlagPcm)
            {
                return Result.Failure<List<EaGranule>>($"Unknown frame flag {flag:X2} at {position}.");
            }

            int version = common >> 6;
            int rateIndex = (common >> 4) & 3;
            if ((version != 3 && version != 2) || rateIndex == 3)
            {
                return Result.Failure<List<EaGranule>>($"Unknown frame header {common:X2} at {position}.");
            }

            int channelMode = (common >> 2) & 3;
            bool mpeg1 = version == 3;
            int channels = channelMode == 3 ? 1 : 2;
            int sideBits = mpeg1 ? 47 : 51;
            long bit = (position + 2) * 8L;
            long limit = end * 8L;
            int index = (int)TakeBits(data, ref bit, 1, limit);
            var scfsi = new int[channels];
            for (int c = 0; c < channels; c++)
            {
                scfsi[c] = mpeg1 && index == 1 ? (int)TakeBits(data, ref bit, 4, limit) : 0;
            }

            var lengths = new int[channels];
            var sides = new ulong[channels];
            for (int c = 0; c < channels; c++)
            {
                lengths[c] = (int)TakeBits(data, ref bit, 12, limit);
                sides[c] = TakeBits(data, ref bit, sideBits, limit);
            }

            int mainBits = lengths.Sum();
            if (bit + mainBits > limit)
            {
                return Result.Failure<List<EaGranule>>($"A frame's data runs past the end of the audio at {position}.");
            }

            var main = new byte[(mainBits + 7) / 8];
            CopyBits(data, bit, main, 0, mainBits);
            bit += mainBits;
            position = (int)((bit + 7) / 8);

            var granule = new EaGranule
            {
                Version = version,
                RateIndex = rateIndex,
                ChannelMode = channelMode,
                ModeExtension = common & 3,
                Index = index,
                Scfsi = scfsi,
                MainBitLengths = lengths,
                SideBits = sides,
                Main = main,
            };

            if (flag == FlagPcm)
            {
                if (position + 8 > end)
                {
                    return Result.Failure<List<EaGranule>>("A raw sample frame runs past the end of the audio.");
                }

                int count = (data[position + 2] << 8) | data[position + 3];
                position += 8;
                int total = count * channels;
                if (position + (total * 2) > end)
                {
                    return Result.Failure<List<EaGranule>>("A raw sample frame runs past the end of the audio.");
                }

                var pcm = new short[total];
                for (int i = 0; i < total; i++)
                {
                    pcm[i] = (short)((data[position + (i * 2)] << 8) | data[position + (i * 2) + 1]);
                }

                position += total * 2;
                granule.Pcm = pcm;
                granule.PcmSamples = count;
            }

            frames.Add(granule);
        }

        return Result.Success(frames);
    }

    /// <summary>The wrapped form of these granules, as it's stored in a bank.</summary>
    public static byte[] Write(IReadOnlyList<EaGranule> granules)
    {
        using var stream = new MemoryStream();
        foreach (EaGranule granule in granules)
        {
            int sideBits = granule.IsMpeg1 ? 47 : 51;
            int mainBits = granule.MainBitLengths.Sum();
            int headerBits = 1 + (granule.IsMpeg1 && granule.Index == 1 ? 4 * granule.Channels : 0) + (granule.Channels * (12 + sideBits));
            var body = new byte[(headerBits + mainBits + 7) / 8];
            long bit = 0;
            PutBits(body, ref bit, (ulong)granule.Index, 1);
            if (granule.IsMpeg1 && granule.Index == 1)
            {
                for (int c = 0; c < granule.Channels; c++)
                {
                    PutBits(body, ref bit, (ulong)granule.Scfsi[c], 4);
                }
            }

            for (int c = 0; c < granule.Channels; c++)
            {
                PutBits(body, ref bit, (ulong)granule.MainBitLengths[c], 12);
                PutBits(body, ref bit, granule.SideBits[c], sideBits);
            }

            CopyBits(granule.Main, 0, body, bit, mainBits);

            stream.WriteByte((byte)(granule.Pcm is null ? FlagPlain : FlagPcm));
            stream.WriteByte((byte)((granule.Version << 6) | (granule.RateIndex << 4) | (granule.ChannelMode << 2) | granule.ModeExtension));
            stream.Write(body);
            if (granule.Pcm is { } pcm)
            {
                stream.Write([0, 0, (byte)(granule.PcmSamples >> 8), (byte)granule.PcmSamples, 0, 0, 0, 0]);
                foreach (short sample in pcm)
                {
                    stream.WriteByte((byte)(sample >> 8));
                    stream.WriteByte((byte)sample);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Standard MPEG frames holding these granules, at the top bitrate so the data always fits, which any Layer III
    /// decoder can then play. A lone last granule of an MPEG-1 stream is paired with a silent one.
    /// </summary>
    public static Result<byte[]> ToMpeg(IReadOnlyList<EaGranule> granules)
    {
        if (granules.Count == 0)
        {
            return Result.Failure<byte[]>("There's no audio here.");
        }

        EaGranule first = granules[0];
        bool mpeg1 = first.IsMpeg1;
        int rate = first.SampleRate;
        int channels = first.Channels;
        int perFrame = mpeg1 ? 2 : 1;
        int frameBytes = mpeg1 ? 144 * 320000 / rate : 72 * 160000 / rate;
        int sideBytes = mpeg1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
        int roomInFrame = frameBytes - 4 - sideBytes;
        int reservoir = mpeg1 ? 511 : 255;
        int frameCount = (granules.Count + perFrame - 1) / perFrame;

        // All the frames' main data is one continuous run of bytes, with each frame's data starting where it fits.
        long totalBits = granules.Sum(g => (long)g.MainBitLengths.Sum());
        var run = new byte[Math.Max(frameCount * roomInFrame, (totalBits + 7) / 8) + (frameCount * 2)];
        var starts = new int[frameCount];
        long written = 0;
        for (int f = 0; f < frameCount; f++)
        {
            int frameStart = f * roomInFrame;
            int start = (int)Math.Max((written + 7) / 8, frameStart - reservoir);
            if (start > frameStart)
            {
                return Result.Failure<byte[]>("This audio doesn't fit standard frames.");
            }

            starts[f] = start;
            written = start * 8L;
            for (int g = f * perFrame; g < Math.Min((f + 1) * perFrame, granules.Count); g++)
            {
                int bits = granules[g].MainBitLengths.Sum();
                CopyBits(granules[g].Main, 0, run, written, bits);
                written += bits;
            }
        }

        int rateIndex = first.RateIndex;
        var output = new byte[frameCount * frameBytes];
        for (int f = 0; f < frameCount; f++)
        {
            int at = f * frameBytes;
            EaGranule lead = granules[f * perFrame];
            output[at] = 0xFF;
            output[at + 1] = (byte)(mpeg1 ? 0xFB : 0xF3);
            output[at + 2] = (byte)((14 << 4) | (rateIndex << 2));
            output[at + 3] = (byte)((lead.ChannelMode << 6) | (lead.ModeExtension << 4));

            long bit = (at + 4) * 8L;
            int back = (f * roomInFrame) - starts[f];
            if (mpeg1)
            {
                PutBits(output, ref bit, (ulong)back, 9);
                bit += channels == 1 ? 5 : 3;
                EaGranule? second = (f * 2) + 1 < granules.Count ? granules[(f * 2) + 1] : null;
                for (int c = 0; c < channels; c++)
                {
                    PutBits(output, ref bit, (ulong)(second?.Scfsi[c] ?? 0), 4);
                }
            }
            else
            {
                PutBits(output, ref bit, (ulong)back, 8);
                bit += channels == 1 ? 1 : 2;
            }

            int sideBits = mpeg1 ? 47 : 51;
            for (int g = f * perFrame; g < (f + 1) * perFrame; g++)
            {
                for (int c = 0; c < channels; c++)
                {
                    if (g < granules.Count)
                    {
                        PutBits(output, ref bit, (ulong)granules[g].MainBitLengths[c], 12);
                        PutBits(output, ref bit, granules[g].SideBits[c], sideBits);
                    }
                    else
                    {
                        bit += 12 + sideBits;
                    }
                }
            }

            Array.Copy(run, f * roomInFrame, output, at + 4 + sideBytes, roomInFrame);
        }

        return Result.Success(output);
    }

    /// <summary>Unpacks a standard MP3 into granules, following its bit reservoir back to where each one's data really is.</summary>
    public static Result<List<EaGranule>> FromMpeg(byte[] mp3)
    {
        int[] bitrates1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] bitrates2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        var granules = new List<EaGranule>();
        var run = new MemoryStream();
        int position = 0;
        while (position + 4 <= mp3.Length)
        {
            if (mp3[position] != 0xFF || (mp3[position + 1] & 0xE6) != 0xE2)
            {
                position++;
                continue;
            }

            int versionBits = (mp3[position + 1] >> 3) & 3;
            bool crc = (mp3[position + 1] & 1) == 0;
            int bitrateIndex = mp3[position + 2] >> 4;
            int rateIndex = (mp3[position + 2] >> 2) & 3;
            int padding = (mp3[position + 2] >> 1) & 1;
            if ((versionBits != 3 && versionBits != 2) || bitrateIndex is 0 or 15 || rateIndex == 3)
            {
                position++;
                continue;
            }

            bool mpeg1 = versionBits == 3;
            int rate = (mpeg1 ? EaGranule.MpegRates1 : EaGranule.MpegRates2)[rateIndex];
            int kbps = (mpeg1 ? bitrates1 : bitrates2)[bitrateIndex];
            int frameBytes = ((mpeg1 ? 144 : 72) * kbps * 1000 / rate) + padding;
            if (position + frameBytes > mp3.Length)
            {
                break;
            }

            int channelMode = mp3[position + 3] >> 6;
            int modeExtension = (mp3[position + 3] >> 4) & 3;
            int channels = channelMode == 3 ? 1 : 2;
            int sideBytes = mpeg1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
            int sideAt = position + 4 + (crc ? 2 : 0);
            long bit = sideAt * 8L;
            long limit = mp3.Length * 8L;
            int back;
            var scfsi = new int[channels];
            if (mpeg1)
            {
                back = (int)TakeBits(mp3, ref bit, 9, limit);
                bit += channels == 1 ? 5 : 3;
                for (int c = 0; c < channels; c++)
                {
                    scfsi[c] = (int)TakeBits(mp3, ref bit, 4, limit);
                }
            }
            else
            {
                back = (int)TakeBits(mp3, ref bit, 8, limit);
                bit += channels == 1 ? 1 : 2;
            }

            int perFrame = mpeg1 ? 2 : 1;
            int sideBits = mpeg1 ? 47 : 51;
            var lengths = new int[perFrame][];
            var sides = new ulong[perFrame][];
            for (int g = 0; g < perFrame; g++)
            {
                lengths[g] = new int[channels];
                sides[g] = new ulong[channels];
                for (int c = 0; c < channels; c++)
                {
                    lengths[g][c] = (int)TakeBits(mp3, ref bit, 12, limit);
                    sides[g][c] = TakeBits(mp3, ref bit, sideBits, limit);
                }
            }

            // This frame's data starts `back` bytes before its own place in the run.
            long frameStart = run.Length;
            int mainAt = sideAt + sideBytes;
            run.Write(mp3, mainAt, position + frameBytes - mainAt);
            if (back > frameStart)
            {
                return Result.Failure<List<EaGranule>>("The MP3's first frame needs data from before the start of the file.");
            }

            byte[] bytes = run.GetBuffer();
            long read = (frameStart - back) * 8;
            for (int g = 0; g < perFrame; g++)
            {
                int bits = lengths[g].Sum();
                if (read + bits > run.Length * 8)
                {
                    return Result.Failure<List<EaGranule>>("The MP3's frames don't add up.");
                }

                var main = new byte[(bits + 7) / 8];
                CopyBits(bytes, read, main, 0, bits);
                read += bits;
                granules.Add(new EaGranule
                {
                    Version = versionBits,
                    RateIndex = rateIndex,
                    ChannelMode = channelMode,
                    ModeExtension = modeExtension,
                    Index = g,
                    Scfsi = g == 1 ? scfsi : new int[channels],
                    MainBitLengths = lengths[g],
                    SideBits = sides[g],
                    Main = main,
                });
            }

            position += frameBytes;
        }

        return granules.Count > 0 ? Result.Success(granules) : Result.Failure<List<EaGranule>>("No MPEG frames were found.");
    }

    private static ulong TakeBits(byte[] data, ref long bit, int count, long limit)
    {
        if (bit + count > limit)
        {
            throw new ArgumentException("The audio ends part way through a frame.");
        }

        ulong value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)((data[bit >> 3] >> (7 - (int)(bit & 7))) & 1);
            bit++;
        }

        return value;
    }

    private static void PutBits(byte[] data, ref long bit, ulong value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                data[bit >> 3] |= (byte)(0x80 >> (int)(bit & 7));
            }

            bit++;
        }
    }

    private static void CopyBits(byte[] from, long fromBit, byte[] to, long toBit, int count)
    {
        // Whole bytes when both sides line up, which is the usual case for a run of main data.
        if (((fromBit | toBit) & 7) == 0 && count >= 8)
        {
            int whole = count / 8;
            Array.Copy(from, fromBit >> 3, to, toBit >> 3, whole);
            fromBit += whole * 8L;
            toBit += whole * 8L;
            count -= whole * 8;
        }

        for (int i = 0; i < count; i++)
        {
            if (((from[fromBit >> 3] >> (7 - (int)(fromBit & 7))) & 1) != 0)
            {
                to[toBit >> 3] |= (byte)(0x80 >> (int)(toBit & 7));
            }

            fromBit++;
            toBit++;
        }
    }
}
