namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>Writes/reads plain PCM .wav files -- used both for exporting a decoded preview and
/// for staging a replacement source (after any WAV/MP3 -> PCM conversion) before re-encoding.</summary>
public static class WavFile
{
    public static byte[] Write(AbkPcmAudio audio)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int byteRate = audio.SampleRate * audio.Channels * 2;
        short blockAlign = (short)(audio.Channels * 2);

        bw.Write("RIFF"u8);
        bw.Write(36 + audio.Pcm16.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)audio.Channels);
        bw.Write(audio.SampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(audio.Pcm16.Length);
        bw.Write(audio.Pcm16);

        return ms.ToArray();
    }

    /// <summary>Reads a plain PCM .wav file's fmt/data chunks. Does not handle compressed WAV variants.</summary>
    public static AbkPcmAudio? Read(byte[] bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
        {
            return null;
        }

        int sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        int pos = 12;

        while (pos + 8 <= bytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
            int chunkDataStart = pos + 8;
            if (chunkDataStart + chunkSize > bytes.Length)
            {
                break;
            }

            if (chunkId == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, chunkDataStart + 2);
                sampleRate = BitConverter.ToInt32(bytes, chunkDataStart + 4);
                bitsPerSample = BitConverter.ToInt16(bytes, chunkDataStart + 14);
            }
            else if (chunkId == "data" && bitsPerSample == 16 && channels > 0 && sampleRate > 0)
            {
                var pcm = new byte[chunkSize];
                Array.Copy(bytes, chunkDataStart, pcm, 0, chunkSize);
                return new AbkPcmAudio { SampleRate = sampleRate, Channels = channels, Pcm16 = pcm };
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2);
        }

        return null;
    }
}
