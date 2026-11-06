using System.Diagnostics;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// Replaces one sound slot in an Xbox 360 (XMA2/codec 3) .abk file. Unlike the PS3 path
/// (<see cref="AbkPs3Writer"/>), there is no existing tool that understands codec 3 in this
/// container -- the user's ABK_insert.exe only ever knew about EA-XAS. This ports its
/// relocation algorithm (decompiled from ABK_Insert.dll) directly: copy the header block
/// verbatim, rewrite the target sound's own region, copy the tail verbatim, then patch the
/// six outer size fields and the trailing offset table by the resulting size delta.
///
/// Audio encoding uses Microsoft's real XDK xma2encode.exe (the same tool NHL Legacy
/// Edition's own asset pipeline would have used), not a reimplementation -- there's no
/// realistic path to writing a from-scratch XMA2 encoder.
///
/// The 4-byte field between the chunk header and the raw XMA2 payload (see
/// <see cref="AbkContainer"/>'s doc comment) has a confirmed formula: payloadBytes*4 + (19 -
/// channels). This was solved from 4 real data points across two files (the one PS3/360 sample
/// pair, plus all 3 sounds in 026_chant.abk -- a stereo/32kHz file, unlike the mono/44.1kHz
/// sample) and matches all 4 exactly: +18 for the mono sound, +17 for all three stereo ones.
/// An earlier guess here (a rough proportional estimate, off by ~18 out of ~495000) was
/// confirmed in-game to corrupt/crash the game; this formula has since been confirmed in-game
/// to produce a working replacement. The field's underlying *meaning* is still inferred from
/// fitting a pattern rather than from documentation, so a channel count or sample rate outside
/// what's been observed (mono/44.1kHz, stereo/32kHz) is unverified territory.
/// </summary>
public static class AbkXbox360Writer
{
    private const uint DummyOffset = 0xFFFFFFFF;

    public static Result<string> ReplaceSound(
        string sourceAbkPath,
        string replacementAudioPath,
        int sampleIndex,
        string outputAbkPath,
        string ffmpegExePath,
        string xma2EncodeExePath)
    {
        if (!File.Exists(ffmpegExePath))
        {
            return Result.Failure<string>($"ffmpeg.exe not found at '{ffmpegExePath}'.");
        }

        if (!File.Exists(xma2EncodeExePath))
        {
            return Result.Failure<string>($"xma2encode.exe not found at '{xma2EncodeExePath}'.");
        }

        byte[] original = File.ReadAllBytes(sourceAbkPath);
        Result<AbkFile> parsed = AbkContainer.Parse(original);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<string>(parsed.Error!);
        }

        AbkSound? sound = parsed.Value!.Sounds.FirstOrDefault(s => s.Index == sampleIndex && !s.IsDummy);
        if (sound is null)
        {
            return Result.Failure<string>($"Sound {sampleIndex} not found (or is an empty slot).");
        }

        if (sound.Codec != AbkCodecKind.Xma2)
        {
            return Result.Failure<string>($"Sound {sampleIndex} is not an XMA2 (Xbox 360) sound.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "NhlWorkbench_AbkXbox360Write_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string pcmWav = Path.Combine(tempDir, "source_pcm.wav");
            Result<string> convert = RunProcess(ffmpegExePath, null,
                "-y", "-i", replacementAudioPath, "-ac", sound.Channels.ToString(), "-ar", sound.SampleRate.ToString(),
                "-sample_fmt", "s16", "-acodec", "pcm_s16le", pcmWav);
            if (!convert.IsSuccess)
            {
                return Result.Failure<string>($"Converting '{replacementAudioPath}' to PCM failed: {convert.Error}");
            }

            string xmaPath = Path.Combine(tempDir, "encoded.xma");
            Result<string> encode = RunProcess(xma2EncodeExePath, tempDir, pcmWav, "/TargetFile", xmaPath);
            if (!encode.IsSuccess)
            {
                return Result.Failure<string>($"xma2encode.exe failed: {encode.Error}");
            }

            Result<(byte[] Payload, int SampleCount)> xmaResult = ReadXmaPayload(xmaPath);
            if (!xmaResult.IsSuccess)
            {
                return Result.Failure<string>(xmaResult.Error!);
            }

            (byte[] newPayload, int newSampleCount) = xmaResult.Value;

            Result<byte[]> rebuilt = Rebuild(original, sound, newPayload, newSampleCount);
            if (!rebuilt.IsSuccess)
            {
                return Result.Failure<string>(rebuilt.Error!);
            }

            File.WriteAllBytes(outputAbkPath, rebuilt.Value!);
            return Result.Success(outputAbkPath);
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"Replacing sound {sampleIndex} failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Extracts the raw compressed payload and true sample count from a real XMA2 WAV
    /// as produced by xma2encode.exe (standard fmt-tag-0x166 WAVEFORMATEX, not the "XMA2" chunk
    /// variant <see cref="Xma2Decoder"/> writes for ffmpeg's benefit).</summary>
    private static Result<(byte[] Payload, int SampleCount)> ReadXmaPayload(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
        {
            return Result.Failure<(byte[], int)>("xma2encode.exe output is not a valid WAV/XMA2 file.");
        }

        int pos = 12;
        int sampleCount = -1;
        while (pos + 8 <= bytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "fmt " && chunkSize >= 18 + 34)
            {
                // XMA2WAVEFORMATEX: base WAVEFORMATEX (18 bytes) + XMA2-specific extension.
                // SamplesEncoded is a u32 at extension offset +6 (NumStreams:2, ChannelMask:4, SamplesEncoded:4).
                sampleCount = BitConverter.ToInt32(bytes, chunkDataStart + 18 + 6);
            }
            else if (chunkId == "data")
            {
                if (sampleCount < 0)
                {
                    return Result.Failure<(byte[], int)>("xma2encode.exe output had a 'data' chunk before a recognizable XMA2 'fmt ' chunk.");
                }

                var payload = new byte[chunkSize];
                Array.Copy(bytes, chunkDataStart, payload, 0, chunkSize);
                return Result.Success((payload, sampleCount));
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2);
        }

        return Result.Failure<(byte[], int)>("xma2encode.exe output WAV had no 'data' chunk.");
    }

    private static Result<byte[]> Rebuild(byte[] original, AbkSound sound, byte[] newPayload, int newSampleCount)
    {
        // Re-derive the outer header fields and this sound's per-sound header position exactly
        // as AbkContainer.Parse does, since we need to edit them, not just read them.
        long bankStart = BigEndianReaderInt(original, 24);
        long headerPos = sound.HeaderOffset;

        uint packedWord = ReadUInt32LE(original, (int)headerPos);
        long secondWordPos = headerPos + 4;
        uint secondWordRaw = ReadUInt32LE(original, (int)secondWordPos);
        byte loopFlags = (byte)(secondWordRaw & 0xF0);

        long chunkTablePos = secondWordPos + 4;
        if ((loopFlags & 0x20) != 0)
        {
            chunkTablePos += 4;
            if ((loopFlags & 0x40) != 0)
            {
                chunkTablePos += 4;
            }
        }

        // Our one real codec-3 sample has exactly one chunk; find where its region ends by
        // replaying the same accumulation Parse does, so this still works if that chunk's
        // declared size doesn't consume the whole payload for some other sample.
        uint totalSamplesOld = ByteSwap(secondWordRaw & 0xFFFFFF0F);
        long cursor = chunkTablePos;
        uint accumulated = 0;
        while (accumulated < totalSamplesOld)
        {
            uint chunkSize = ReadUInt32BE(original, (int)cursor);
            uint chunkSampleCount = ReadUInt32BE(original, (int)(cursor + 4));
            accumulated += chunkSampleCount;
            cursor += chunkSize;
        }

        long oldRegionEnd = cursor; // one past this sound's last chunk
        long oldRegionLength = oldRegionEnd - headerPos;

        // New region: [packed word][second word with updated sample count][chunkSize][chunkSampleCount][4-byte field][payload]
        const int quirkFieldLength = 4;
        int newChunkSize = 8 + quirkFieldLength + newPayload.Length; // inclusive of its own 8-byte header
        var newRegion = new byte[8 + newChunkSize];

        WriteUInt32LE(newRegion, 0, packedWord); // codec/sample-rate/channels unchanged

        // Mirrors AbkContainer's read side exactly: totalSamples = ByteSwap(raw & ~0xF0), loop
        // flags = raw & 0xF0. ByteSwap(newSampleCount)'s own bits 4-7 (== original bits 28-31,
        // always 0 for any realistic sample count) can't collide with the flag bits, so a plain
        // OR round-trips losslessly the same way the encoder's own bit-packing does.
        uint newSecondWordRaw = ByteSwap((uint)newSampleCount) | (secondWordRaw & 0xF0);
        WriteUInt32LE(newRegion, 4, newSecondWordRaw);
        WriteUInt32BE(newRegion, 8, (uint)newChunkSize);
        WriteUInt32BE(newRegion, 12, (uint)newSampleCount);

        // Confirmed formula -- see the type doc comment for how this was solved and verified
        // across 4 real sounds (2 files, mono and stereo).
        uint quirkValue = (uint)((newPayload.Length * 4) + (19 - sound.Channels));
        WriteUInt32BE(newRegion, 16, quirkValue);

        Array.Copy(newPayload, 0, newRegion, 20, newPayload.Length);

        long delta = newRegion.Length - oldRegionLength;

        long num = BigEndianReaderInt(original, 20);
        long num2 = bankStart;
        long num3 = BigEndianReaderInt(original, 36);
        long num4 = BigEndianReaderInt(original, 48);
        long num5 = BigEndianReaderInt(original, 52);
        long num6 = BigEndianReaderInt(original, 56);

        using var output = new MemoryStream();

        // 1) Header block, verbatim, up to the bank start.
        output.Write(original, 0, (int)num2);

        // 2) Bytes from the bank start up to this sound's header, verbatim (magic, sound count,
        // full offset table, any earlier sounds' regions).
        output.Write(original, (int)num2, (int)(headerPos - num2));

        // 3) This sound's newly-built region.
        output.Write(newRegion, 0, newRegion.Length);

        // 4) Everything after this sound's old region up to num4 (later sounds' regions, if any),
        // verbatim -- their own offset-table entries are patched in step 6.
        output.Write(original, (int)oldRegionEnd, (int)(num4 - oldRegionEnd));

        // 5) The tail (from num4 to EOF), verbatim.
        long tailLength = original.Length - num4;
        output.Write(original, (int)num4, (int)tailLength);

        byte[] result = output.ToArray();

        // 6) Patch the six outer size fields by the delta.
        WriteUInt32BEAt(result, 20, (uint)(num + delta));
        WriteUInt32BEAt(result, 36, (uint)(num3 + delta));
        WriteUInt32BEAt(result, 48, (uint)(num4 + delta));
        WriteUInt32BEAt(result, 52, (uint)(num5 + delta));
        WriteUInt32BEAt(result, 56, (uint)(num6 + delta));

        // 7) Patch this sound's own offset-table entry only if its region moved (it doesn't --
        // its own header position is unchanged; only what comes after it shifts) -- but any
        // *later* sound's offset-table entry needs += delta, and the trailing relocation table
        // living at the (now-shifted) num6 position needs the same treatment.
        long offsetTableBase = num2 + 12;
        uint soundCount = ReadUInt32BE(result, (int)(num2 + 8));
        for (int i = sound.Index + 1; i < soundCount; i++)
        {
            long entryPos = offsetTableBase + (i * 4);
            uint entry = ReadUInt32BE(result, (int)entryPos);
            if (entry != DummyOffset)
            {
                WriteUInt32BEAt(result, (int)entryPos, (uint)(entry + delta));
            }
        }

        long relocTablePos = num6 + delta;
        if (relocTablePos + 4 <= result.Length)
        {
            uint recordCount = ReadUInt32BE(result, (int)relocTablePos);
            long recordPos = relocTablePos + 4;
            for (int i = 0; i < recordCount && recordPos + 12 <= result.Length; i++)
            {
                uint offsetField = ReadUInt32BE(result, (int)(recordPos + 4));
                WriteUInt32BEAt(result, (int)(recordPos + 4), (uint)(offsetField + delta));
                recordPos += 12;
            }
        }

        return Result.Success(result);
    }

    private static Result<string> RunProcess(string exePath, string? workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return Result.Failure<string>($"Could not start '{exePath}'.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            return Result.Failure<string>($"exit code {process.ExitCode}: {stdout}\n{stderr}");
        }

        return Result.Success(stdout);
    }

    private static long BigEndianReaderInt(byte[] data, int offset) => ReadUInt32BE(data, offset);

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteUInt32BEAt(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteUInt32BE(byte[] data, int offset, uint value) => WriteUInt32BEAt(data, offset, value);

    private static uint ReadUInt32LE(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static uint ByteSwap(uint value) =>
        ((value & 0x000000FF) << 24) |
        ((value & 0x0000FF00) << 8) |
        ((value & 0x00FF0000) >> 8) |
        ((value & 0xFF000000) >> 24);
}
