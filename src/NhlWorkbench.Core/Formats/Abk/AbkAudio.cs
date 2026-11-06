using System.Diagnostics;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>A replacement ready to go in a bank: the codec bytes and how many samples they hold.</summary>
public sealed record EncodedSound(byte[] Payload, int Samples);

/// <summary>
/// Turns the sounds in a bank into plain samples and back, using the ffmpeg that comes with the game for the actual MPEG
/// decoding and MP3 encoding. No other tool is needed, and nothing but temporary files gets written.
/// </summary>
public static class AbkAudio
{
    /// <summary>Plays one sound. <paramref name="ast"/> is the bank's stream file, needed only for streamed sounds.</summary>
    public static Result<AbkPcmAudio> Decode(AbkBank bank, AbkRecord record, byte[]? ast, string? ffmpeg)
    {
        try
        {
            if (record.CodecId == (int)AbkCodecKind.EaXas && !record.IsStream && record.Segments.Count > 0)
            {
                return EaXasDecoder.Decode(bank.Data, record.Segments[0].Offset, record.SampleCount, record.Channels, record.SampleRate);
            }

            if (record.CodecId != 5)
            {
                return Result.Failure<AbkPcmAudio>($"This sound uses codec {record.CodecId}, which can't be played here yet.");
            }

            if (ffmpeg is null)
            {
                return Result.Failure<AbkPcmAudio>("Playing sounds needs the ffmpeg that comes with NHL Legacy Native. Connect your game folder rather than a disc image.");
            }

            Result<List<(byte[] Data, int Offset, int Length)>> parts = Parts(bank, record, ast);
            if (!parts.IsSuccess)
            {
                return Result.Failure<AbkPcmAudio>(parts.Error!);
            }

            List<(byte[] Data, int Offset, int Length)> blocks = parts.Value!;

            // A looped sound opens with a short block whose only sound is its raw samples.
            short[][] stub = [];
            if (record.IsLooped && blocks.Count > 1)
            {
                (byte[] data, int offset, int length) = blocks[0];
                Result<List<EaGranule>> stubFrames = EaLayer3.Read(data, offset, length);
                if (!stubFrames.IsSuccess)
                {
                    return Result.Failure<AbkPcmAudio>(stubFrames.Error!);
                }

                stub = Split(stubFrames.Value!, StreamsIn(record)).Select(RawSamples).ToArray();
                blocks = blocks.Skip(1).ToList();
            }

            var granules = new List<EaGranule>();
            foreach ((byte[] data, int offset, int length) in blocks)
            {
                Result<List<EaGranule>> frames = EaLayer3.Read(data, offset, length);
                if (!frames.IsSuccess)
                {
                    return Result.Failure<AbkPcmAudio>(frames.Error!);
                }

                granules.AddRange(frames.Value!);
            }

            int streams = StreamsIn(record);
            List<List<EaGranule>> split = Split(granules, streams);
            var decoded = new List<short[]>();
            int perStream = 0;
            for (int s = 0; s < streams; s++)
            {
                Result<short[]> samples = DecodeStream(split[s], s < stub.Length ? stub[s] : [], ffmpeg);
                if (!samples.IsSuccess)
                {
                    return Result.Failure<AbkPcmAudio>(samples.Error!);
                }

                decoded.Add(samples.Value!);
                perStream = split[s].Count > 0 ? split[s][0].Channels : 1;
            }

            // Each stream's samples side by side, cut to the length the bank declares.
            int channels = perStream * streams;
            int count = Math.Min(record.SampleCount, decoded.Min(d => d.Length / perStream));
            var pcm = new byte[count * channels * 2];
            int write = 0;
            for (int i = 0; i < count; i++)
            {
                foreach (short[] stream in decoded)
                {
                    for (int c = 0; c < perStream; c++)
                    {
                        short sample = stream[(i * perStream) + c];
                        pcm[write++] = (byte)sample;
                        pcm[write++] = (byte)(sample >> 8);
                    }
                }
            }

            return Result.Success(new AbkPcmAudio { SampleRate = record.SampleRate, Channels = channels, Pcm16 = pcm });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<AbkPcmAudio>($"This sound couldn't be played: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Encodes any audio file ffmpeg can read for a slot with this rate and channel count: to MP3 first, which the game's
    /// codec is a rewrapping of, with the raw lead-in samples a sound opens with taken from the start of the audio.
    /// </summary>
    public static Result<EncodedSound> Encode(string audioFile, int sampleRate, int channels, string ffmpeg, SoundShape? shape = null)
    {
        if (channels is not (1 or 2 or 4))
        {
            return Result.Failure<EncodedSound>("Only mono, stereo and four-channel sounds can be replaced.");
        }

        // A four-channel sound is two stereo streams taking turns. The new audio goes on both.
        int streams = channels == 4 ? 2 : 1;
        channels /= streams;

        string folder = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Sound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Plain samples first, to know exactly how long it is and to take the lead-in from.
            string rate = sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string channelText = channels.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string raw = Path.Combine(folder, "plain.raw");
            Result<bool> converted = Run(ffmpeg, "-i", audioFile, "-vn", "-ar", rate, "-ac", channelText, "-f", "s16le", "-acodec", "pcm_s16le", raw);
            if (!converted.IsSuccess)
            {
                return Result.Failure<EncodedSound>($"That file couldn't be read as audio: {converted.Error}");
            }

            byte[] plain = File.ReadAllBytes(raw);
            if (shape is { DoesNothing: false })
            {
                plain = shape.Apply(plain, sampleRate, channels);
            }

            int samples = plain.Length / (2 * channels);
            if (samples < 576)
            {
                return Result.Failure<EncodedSound>("That file has almost no audio in it.");
            }

            // Silence after the end, so the coder has flushed everything before the frames are cut.
            string padded = Path.Combine(folder, "padded.raw");
            using (FileStream stream = File.Create(padded))
            {
                stream.Write(plain);
                stream.Write(new byte[4 * 1152 * 2 * channels]);
            }

            string mp3 = Path.Combine(folder, "coded.mp3");
            Result<bool> coded = Run(ffmpeg, "-f", "s16le", "-ar", rate, "-ac", channelText, "-i", padded,
                "-c:a", "libmp3lame", "-b:a", channels == 1 ? "128k" : "192k", "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3", mp3);
            if (!coded.IsSuccess)
            {
                return Result.Failure<EncodedSound>($"The sound couldn't be encoded: {coded.Error}");
            }

            Result<List<EaGranule>> granules = EaLayer3.FromMpeg(File.ReadAllBytes(mp3));
            if (!granules.IsSuccess)
            {
                return Result.Failure<EncodedSound>(granules.Error!);
            }

            List<EaGranule> all = granules.Value!;
            // The game's own sounds carry the warm-up granules and two or three more after the end.
            int needed = ((samples - EaLayer3.LeadInSamples + 575) / 576) + 4;
            if (all[0].IsMpeg1 && needed % 2 != 0)
            {
                needed++;
            }

            if (all.Count < needed)
            {
                return Result.Failure<EncodedSound>("The encoder gave back less audio than it was given.");
            }

            List<EaGranule> kept = all.Take(needed).ToList();
            var lead = new short[EaLayer3.LeadInSamples * channels];
            for (int i = 0; i < EaLayer3.LeadInSamples; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int at = ((i * channels) + c) * 2;
                    lead[(c * EaLayer3.LeadInSamples) + i] = (short)(plain[at] | (plain[at + 1] << 8));
                }
            }

            kept[1].Pcm = lead;
            kept[1].PcmSamples = EaLayer3.LeadInSamples;
            if (streams > 1)
            {
                kept = kept.SelectMany(g => Enumerable.Repeat(g, streams)).ToList();
            }

            return Result.Success(new EncodedSound(EaLayer3.Write(kept), samples));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result.Failure<EncodedSound>($"The sound couldn't be encoded: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover in the temp folder does no harm.
            }
        }
    }

    private static int StreamsIn(AbkRecord record) => record.ChannelConfig switch { 3 => 2, 5 => 3, _ => 1 };

    /// <summary>Several streams take turns, frame by frame. This much is inferred, since no reference covers it.</summary>
    private static List<List<EaGranule>> Split(List<EaGranule> granules, int streams)
    {
        var split = Enumerable.Range(0, streams).Select(_ => new List<EaGranule>()).ToList();
        for (int i = 0; i < granules.Count; i++)
        {
            split[i % streams].Add(granules[i]);
        }

        return split;
    }

    /// <summary>The raw opening samples a stream carries, interleaved.</summary>
    private static short[] RawSamples(List<EaGranule> granules)
    {
        foreach (EaGranule granule in granules)
        {
            if (granule.Pcm is { } pcm)
            {
                int channels = granule.Channels;
                int count = granule.PcmSamples;
                var interleaved = new short[count * channels];
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        interleaved[(i * channels) + c] = pcm[(c * count) + i];
                    }
                }

                return interleaved;
            }
        }

        return [];
    }

    private static Result<short[]> DecodeStream(List<EaGranule> granules, short[] lead, string ffmpeg)
    {
        Result<byte[]> mpeg = EaLayer3.ToMpeg(granules);
        if (!mpeg.IsSuccess)
        {
            return Result.Failure<short[]>(mpeg.Error!);
        }

        string folder = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Sound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "stream.mp3");
            string output = Path.Combine(folder, "stream.raw");
            File.WriteAllBytes(input, mpeg.Value!);
            Result<bool> ran = Run(ffmpeg, "-f", "mp3", "-i", input, "-f", "s16le", "-acodec", "pcm_s16le", output);
            if (!ran.IsSuccess)
            {
                return Result.Failure<short[]>($"The sound couldn't be decoded: {ran.Error}");
            }

            byte[] raw = File.ReadAllBytes(output);
            int channels = granules[0].Channels;
            short[] opening = RawSamples(granules);
            int skip = EaLayer3.WarmUpSamples * channels;
            int bodyCount = Math.Max(0, (raw.Length / 2) - skip);
            var samples = new short[lead.Length + opening.Length + bodyCount];
            lead.CopyTo(samples, 0);
            opening.CopyTo(samples, lead.Length);
            int at = lead.Length + opening.Length;
            for (int i = 0; i < bodyCount; i++)
            {
                int from = (skip + i) * 2;
                samples[at + i] = (short)(raw[from] | (raw[from + 1] << 8));
            }

            return Result.Success(samples);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover in the temp folder does no harm.
            }
        }
    }

    /// <summary>Where a sound's codec bytes are: in the bank, or in blocks of its .ast file.</summary>
    private static Result<List<(byte[] Data, int Offset, int Length)>> Parts(AbkBank bank, AbkRecord record, byte[]? ast)
    {
        var parts = new List<(byte[], int, int)>();
        if (!record.IsStream)
        {
            parts.AddRange(record.Segments.Select(s => (bank.Data, s.Offset, s.Length)));
            return Result.Success(parts);
        }

        if (ast is null)
        {
            return Result.Failure<List<(byte[], int, int)>>("This sound is streamed, and its .ast file wasn't found.");
        }

        if (record.StreamOffset is not { } start || start + 8 > ast.Length)
        {
            return Result.Failure<List<(byte[], int, int)>>("This sound's place in its .ast file wasn't found.");
        }

        int at = (int)start;
        while (true)
        {
            if (at + 8 > ast.Length)
            {
                return Result.Failure<List<(byte[], int, int)>>("This sound's stream stops short.");
            }

            int flag = ast[at];
            int size = (ast[at + 1] << 16) | (ast[at + 2] << 8) | ast[at + 3];
            if ((flag != 0x00 && flag != 0x80) || size < 8 || at + size > ast.Length)
            {
                return Result.Failure<List<(byte[], int, int)>>("This sound's stream is damaged.");
            }

            parts.Add((ast, at + 8, size - 8));
            at += size;
            if (flag == 0x80)
            {
                // A looped stream's lead-in is a short stream of its own, and the body follows at the next boundary.
                if (record.LoopStub is not null && parts.Count == 1)
                {
                    at = (at + 0x3F) & ~0x3F;
                    continue;
                }

                return Result.Success(parts);
            }
        }
    }

    private static Result<bool> Run(string ffmpeg, params string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return Result.Failure<bool>("ffmpeg didn't start.");
        }

        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(errors, output);
        return process.ExitCode == 0 ? Result.Success(true) : Result.Failure<bool>(errors.Result.Trim());
    }
}
