using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class AbkBankTests
{
    private static readonly string Ffmpeg = Path.Combine(
        SampleFileCatalog.RepoRoot, "NHL-Legacy-Native_v1.0.0", "NHL Legacy Native", "ffmpeg", "ffmpeg.exe");

    private static GameFileIndex? OpenDisc() =>
        SampleFileCatalog.Ps3DiscAvailable ? GameInstallLocator.Open(SampleFileCatalog.Ps3Disc).Value : null;

    private static AbkBank Bank(GameFileIndex game, string path) =>
        AbkBank.Parse(game.ReadDecoded(game.Find(path)!.Winner).Value!).Value!;

    private static short[] Samples(AbkPcmAudio audio)
    {
        var samples = new short[audio.Pcm16.Length / 2];
        Buffer.BlockCopy(audio.Pcm16, 0, samples, 0, audio.Pcm16.Length);
        return samples;
    }

    /// <summary>A rising tone in a WAV file, so any shift or corruption shows up against the original.</summary>
    private static (string Path, short[] Samples) MakeTone(string folder, int rate, int channels, int count)
    {
        var samples = new short[count * channels];
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)rate;
            short value = (short)(12000 * Math.Sin(2 * Math.PI * (300 + (200 * t)) * t));
            for (int c = 0; c < channels; c++)
            {
                samples[(i * channels) + c] = c == 0 ? value : (short)(value / 2);
            }
        }

        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        string path = Path.Combine(folder, "tone.wav");
        File.WriteAllBytes(path, WavFile.Write(new AbkPcmAudio { SampleRate = rate, Channels = channels, Pcm16 = bytes }));
        return (path, samples);
    }

    [Fact]
    public void EveryBankOnTheDiscReads()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        var banks = game.Files.Where(f => f.Path.EndsWith(".abk", StringComparison.Ordinal)).ToList();
        Assert.True(banks.Count > 200);
        int sounds = 0;
        foreach (GameFile file in banks)
        {
            var parsed = AbkBank.Parse(game.ReadDecoded(file.Winner).Value!);
            Assert.True(parsed.IsSuccess, $"{file.Path}: {parsed.Error}");
            sounds += parsed.Value!.Records.Count;
            Assert.All(parsed.Value.Records, r => Assert.NotEmpty(r.Name));
        }

        Assert.True(sounds > 4000);
    }

    [Fact]
    public void FramesRewriteToTheSameBytes()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        foreach (string path in new[] { "audio/sfx/000_goal.abk", "audio/sfx/000_chant.abk", "audio/sfx/002_chant.abk" })
        {
            AbkBank bank = Bank(game, path);
            foreach (AbkRecord record in bank.Records)
            {
                AbkSegment segment = record.Segments[^1];
                var frames = EaLayer3.Read(bank.Data, segment.Offset, segment.Length);
                Assert.True(frames.IsSuccess, frames.Error);
                Assert.Equal(bank.Data.AsSpan(segment.Offset, segment.Length).ToArray(), EaLayer3.Write(frames.Value!));
            }
        }
    }

    [Fact]
    public void AGameSoundDecodesToItsDeclaredLengthAndRunsOnSmoothly()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null || !File.Exists(Ffmpeg))
        {
            return;
        }

        AbkBank bank = Bank(game, "audio/sfx/000_goal.abk");
        AbkRecord horn = bank.Records[0];
        var audio = AbkAudio.Decode(bank, horn, null, Ffmpeg);
        Assert.True(audio.IsSuccess, audio.Error);
        short[] samples = Samples(audio.Value!);
        Assert.Equal(horn.SampleCount * audio.Value!.Channels, samples.Length);

        // Real audio, not silence or noise: loud enough, and without jumps far beyond its usual step.
        double rms = Math.Sqrt(samples.Average(s => (double)s * s));
        Assert.True(rms > 500, $"RMS {rms}");
        double meanStep = Enumerable.Range(1, samples.Length - 1).Average(i => Math.Abs(samples[i] - samples[i - 1]));
        Assert.True(meanStep < rms, $"step {meanStep} against RMS {rms}");
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    public void AnEncodedSoundDecodesBackToTheSameAudio(int rate, int channels)
    {
        if (!File.Exists(Ffmpeg))
        {
            return;
        }

        string folder = Directory.CreateTempSubdirectory("abk-tone").FullName;
        try
        {
            (string path, short[] original) = MakeTone(folder, rate, channels, rate * 2);
            var encoded = AbkAudio.Encode(path, rate, channels, Ffmpeg);
            Assert.True(encoded.IsSuccess, encoded.Error);
            Assert.Equal(rate * 2, encoded.Value!.Samples);

            byte[] bankBytes = MakeBank(encoded.Value.Payload, encoded.Value.Samples, rate, channels);
            AbkBank bank = AbkBank.Parse(bankBytes).Value!;
            var decoded = AbkAudio.Decode(bank, bank.Records[0], null, Ffmpeg);
            Assert.True(decoded.IsSuccess, decoded.Error);
            short[] back = Samples(decoded.Value!);
            Assert.Equal(original.Length, back.Length);

            // Not just the same length: lined up to the sample, with only a lossy coder's error between them.
            double signal = 0, noise = 0;
            for (int i = 2000; i < back.Length - 2000; i++)
            {
                signal += (double)original[i] * original[i];
                double difference = original[i] - back[i];
                noise += difference * difference;
            }

            double snr = 10 * Math.Log10(signal / Math.Max(noise, 1));
            Assert.True(snr > 20, $"Signal to noise {snr:0.0} dB");

            // The raw lead-in samples are exact.
            for (int i = 0; i < EaLayer3.LeadInSamples * channels; i++)
            {
                Assert.Equal(original[i], back[i]);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AFourChannelSlotPlaysTheSameAudioOnBothPairs()
    {
        if (!File.Exists(Ffmpeg))
        {
            return;
        }

        string folder = Directory.CreateTempSubdirectory("abk-tone").FullName;
        try
        {
            (string path, short[] original) = MakeTone(folder, 44100, 2, 44100);
            var encoded = AbkAudio.Encode(path, 44100, 4, Ffmpeg);
            Assert.True(encoded.IsSuccess, encoded.Error);
            AbkBank bank = AbkBank.Parse(MakeBank(encoded.Value!.Payload, encoded.Value.Samples, 44100, 4)).Value!;
            var decoded = AbkAudio.Decode(bank, bank.Records[0], null, Ffmpeg);
            Assert.True(decoded.IsSuccess, decoded.Error);
            Assert.Equal(4, decoded.Value!.Channels);
            short[] back = Samples(decoded.Value);
            Assert.Equal(original.Length * 2, back.Length);
            for (int i = 0; i < 4000; i += 4)
            {
                Assert.Equal(back[i], back[i + 2]);
                Assert.Equal(back[i + 1], back[i + 3]);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void BanksGoByTeamWhereAnArenaNamesThem()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        var teams = TeamCatalog.Build(game).Value;
        var banks = NhlWorkbench.Core.Sounds.SoundCatalog.Build(game, teams);
        Assert.True(banks.Count > 200);
        Assert.Contains(banks, b => b.Group == NhlWorkbench.Core.Sounds.SoundCatalog.GoalHorns && b.Teams.Any(t => t.Contains("Penguins", StringComparison.Ordinal)));
        Assert.Contains(banks, b => b.Group == NhlWorkbench.Core.Sounds.SoundCatalog.Chants && b.Teams.Count > 0);
        Assert.Contains(banks, b => b.Group == NhlWorkbench.Core.Sounds.SoundCatalog.GoalieChants);
        Assert.Contains(banks, b => b.Group == NhlWorkbench.Core.Sounds.SoundCatalog.Crowd && b.Title == "crowds");
        Assert.All(banks, b => Assert.NotEmpty(b.Title));
    }

    [Theory]
    [InlineData("audio/sfx/000_goal.abk", 0)]
    [InlineData("audio/sfx/000_chant.abk", 0)]
    [InlineData("audio/sfx/000_chant.abk", 1)]
    [InlineData("audio/sfx/000_chant.abk", 2)]
    public void ReplacingASoundMovesTheRestOfTheBankWithoutChangingIt(string path, int index)
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        AbkBank bank = Bank(game, path);
        AbkRecord target = bank.Records[index];
        foreach (int size in new[] { 1001, 200000 })
        {
            // Not valid audio, only a size. What's checked here is the bank's structure.
            var payload = new byte[size];
            payload[0] = 0xAB;
            var replaced = bank.Replace(target, payload, 12345);
            Assert.True(replaced.IsSuccess, replaced.Error);
            AbkBank after = AbkBank.Parse(replaced.Value!).Value!;

            Assert.Equal(bank.Records.Count, after.Records.Count);
            Assert.Equal(0u, BigEndian(replaced.Value!, 0x30) % 4);
            for (int i = 0; i < bank.Records.Count; i++)
            {
                AbkRecord old = bank.Records[i];
                AbkRecord now = after.Records[i];
                Assert.Equal(old.Name, now.Name);
                Assert.Equal(old.CodecId, now.CodecId);
                Assert.Equal(old.SampleRate, now.SampleRate);
                Assert.Equal(old.IsLooped, now.IsLooped);
                if (i == index)
                {
                    Assert.Equal(12345, now.SampleCount);
                    Assert.Equal(payload, replaced.Value!.AsSpan(now.Segments[0].Offset, now.Segments[0].Length).ToArray());
                }
                else
                {
                    Assert.Equal(old.SampleCount, now.SampleCount);
                    Assert.Equal(
                        bank.Data.AsSpan(old.Segments[^1].Offset, old.Segments[^1].Length).ToArray(),
                        replaced.Value!.AsSpan(now.Segments[^1].Offset, now.Segments[^1].Length).ToArray());
                }
            }

            // Everything before the sound, and the whole tail, match apart from the corrected offsets.
            int directory = (int)BigEndian(replaced.Value!, 0x38);
            int oldDirectory = (int)BigEndian(bank.Data, 0x38);
            Assert.Equal(bank.Data.Length - oldDirectory, replaced.Value!.Length - directory);
        }
    }

    [Fact]
    public void ReplacingALoopedSoundKeepsItLooped()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        AbkBank bank = Bank(game, "audio/sfx/000_chant.abk");
        AbkRecord looped = bank.Records.First(r => r.IsLooped && !r.IsStream);
        byte[] replaced = bank.Replace(looped, new byte[500], 9999).Value!;
        AbkRecord after = AbkBank.Parse(replaced).Value!.Records.First(r => r.Index == looped.Index);
        Assert.True(after.IsLooped);
        Assert.Equal(0, after.LoopStart);
        Assert.Single(after.Segments);
    }

    [Fact]
    public void StreamedSoundsAreRefusedAndGarbageDoesNotThrow()
    {
        Assert.False(AbkBank.Parse([1, 2, 3]).IsSuccess);
        Assert.False(AbkBank.Parse(new byte[100]).IsSuccess);
        byte[] fake = new byte[100];
        "ABKC"u8.CopyTo(fake);
        Assert.False(AbkBank.Parse(fake).IsSuccess);

        using GameFileIndex? game = OpenDisc();
        if (game is null)
        {
            return;
        }

        AbkBank bank = Bank(game, "audio/sfx/crowds.abk");
        AbkRecord stream = bank.Records.First(r => r.IsStream);
        Assert.False(bank.Replace(stream, new byte[10], 10).IsSuccess);
        Assert.False(AbkAudio.Decode(bank, stream, null, Ffmpeg).IsSuccess);
    }

    [Fact]
    public void StreamedSoundsDecodeFromTheirAstFile()
    {
        using GameFileIndex? game = OpenDisc();
        if (game is null || !File.Exists(Ffmpeg))
        {
            return;
        }

        AbkBank bank = Bank(game, "audio/sfx/crowds.abk");
        byte[] ast = game.ReadDecoded(game.Find("audio/sfx/crowds.ast")!.Winner).Value!;
        foreach (AbkRecord record in bank.Records.Where(r => r.IsStream).Take(4))
        {
            var audio = AbkAudio.Decode(bank, record, ast, Ffmpeg);
            Assert.True(audio.IsSuccess, $"{record.Name}: {audio.Error}");
            Assert.Equal(record.SampleCount * audio.Value!.Channels, audio.Value.Pcm16.Length / 2);
        }
    }

    private static uint BigEndian(byte[] data, int at) =>
        (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);

    /// <summary>The smallest bank the parser accepts, with one sound in it.</summary>
    private static byte[] MakeBank(byte[] payload, int samples, int rate, int channels)
    {
        const int table = 0x80;
        int record = table + 16;
        int dataEnd = (record + 16 + payload.Length + 3) & ~3;
        var data = new byte[dataEnd + 16];
        "ABKC"u8.CopyTo(data);
        data[8] = 0x0A;
        Put(data, 0x14, (uint)data.Length);
        Put(data, 0x18, table);
        Put(data, 0x1C, 0x78);
        Put(data, 0x24, (uint)(dataEnd - table));
        Put(data, 0x30, (uint)dataEnd);
        Put(data, 0x34, (uint)dataEnd + 4);
        Put(data, 0x38, (uint)dataEnd + 8);
        "S10A"u8.CopyTo(data.AsSpan(table));
        Put(data, table + 8, 1);
        Put(data, table + 12, 16);
        Put(data, record, 0x15000000u | ((uint)(channels - 1) << 18) | (uint)rate);
        Put(data, record + 4, (uint)samples);
        Put(data, record + 8, (uint)(8 + payload.Length));
        Put(data, record + 12, (uint)samples);
        payload.CopyTo(data, record + 16);
        return data;
    }

    private static void Put(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }
}
