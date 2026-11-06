using System.Diagnostics;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// Decodes Xbox 360 XMA2 audio (codec ID 3) by shelling out to ffmpeg, which has a native
/// XMA2 decoder but no encoder -- there is no realistic path to a dependency-free managed
/// XMA2 decoder given how close it is to WMA Pro. The raw payload is wrapped in a minimal
/// synthetic RIFF/WAVE file carrying an "XMA2" chunk (the layout ffmpeg's own wav demuxer
/// parses, per its source -- not Microsoft's XMA2WAVEFORMATEX struct, which ffmpeg does not
/// use for this chunk type) rather than trying to reconstruct a byte-for-byte original XMA2
/// container. Verified end-to-end against a real 360 sample: clean decode, correct duration,
/// confirmed audible as the source goal horn.
/// </summary>
public static class Xma2Decoder
{
    public static Result<AbkPcmAudio> Decode(byte[] data, long payloadOffset, long payloadLength, int sampleCount, int sampleRate, int channels, string ffmpegExePath)
    {
        if (!File.Exists(ffmpegExePath))
        {
            return Result.Failure<AbkPcmAudio>($"ffmpeg.exe not found at '{ffmpegExePath}'.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "NhlWorkbench_Abk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string inputWav = Path.Combine(tempDir, "in.wav");
        string outputWav = Path.Combine(tempDir, "out.wav");

        try
        {
            byte[] payload = new byte[payloadLength];
            Array.Copy(data, payloadOffset, payload, 0, payloadLength);
            File.WriteAllBytes(inputWav, BuildXma2Wav(payload, sampleCount, sampleRate, channels));

            var psi = new ProcessStartInfo(ffmpegExePath)
            {
                ArgumentList = { "-y", "-i", inputWav, outputWav },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return Result.Failure<AbkPcmAudio>("Could not start ffmpeg.exe.");
            }

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(outputWav))
            {
                return Result.Failure<AbkPcmAudio>($"ffmpeg failed to decode XMA2 audio (exit {process.ExitCode}): {stderr}");
            }

            return ReadWavPcm(outputWav);
        }
        catch (Exception ex)
        {
            return Result.Failure<AbkPcmAudio>($"XMA2 decode failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp folder isn't worth failing the decode over.
            }
        }
    }

    /// <summary>
    /// Builds a RIFF/WAVE file with an "XMA2" chunk in place of "fmt ", matching ffmpeg's
    /// libavformat/wavdec.c wav_parse_xma2_tag layout: version(1) + numStreams(1) + skip(10) +
    /// sampleRate:u32BE + skip(4) + totalSamples:u32BE + skip(8) + per-stream [channels(1) +
    /// skip(3)]. Total chunk size must equal 32 + 4*numStreams for version 3.
    /// </summary>
    private static byte[] BuildXma2Wav(byte[] payload, int sampleCount, int sampleRate, int channels)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var xma2 = new byte[32 + 4];
        xma2[0] = 3; // version
        xma2[1] = 1; // num streams
        WriteUInt32BE(xma2, 12, (uint)sampleRate);
        WriteUInt32BE(xma2, 20, (uint)sampleCount);
        xma2[32] = (byte)channels;

        int dataLen = payload.Length + (payload.Length % 2);
        int riffSize = 4 + (8 + xma2.Length) + (8 + dataLen);

        bw.Write("RIFF"u8);
        bw.Write(riffSize);
        bw.Write("WAVE"u8);
        bw.Write("XMA2"u8);
        bw.Write(xma2.Length);
        bw.Write(xma2);
        bw.Write("data"u8);
        bw.Write(payload.Length);
        bw.Write(payload);
        if (payload.Length % 2 != 0)
        {
            bw.Write((byte)0);
        }

        return ms.ToArray();
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static Result<AbkPcmAudio> ReadWavPcm(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
        {
            return Result.Failure<AbkPcmAudio>("ffmpeg output is not a valid WAV file.");
        }

        int sampleRate = 44100;
        short channels = 1;
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
            int chunkDataStart = pos + 8;

            if (chunkId == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, chunkDataStart + 2);
                sampleRate = BitConverter.ToInt32(bytes, chunkDataStart + 4);
            }
            else if (chunkId == "data")
            {
                var pcm = new byte[chunkSize];
                Array.Copy(bytes, chunkDataStart, pcm, 0, chunkSize);
                return Result.Success(new AbkPcmAudio { SampleRate = sampleRate, Channels = channels, Pcm16 = pcm });
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2);
        }

        return Result.Failure<AbkPcmAudio>("ffmpeg output WAV had no 'data' chunk.");
    }
}
