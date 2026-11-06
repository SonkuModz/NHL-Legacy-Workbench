using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truths the ABKC/S10A parser and both codec decoders against the real PS3 and Xbox
/// 360 samples of the same goal-horn clip. The PS3 (EA-XAS) and 360 (XMA2) decode paths were
/// each confirmed by ear against these exact files before this suite was written -- these
/// tests are a regression guard for that already-verified behavior, not a substitute for it.
/// </summary>
public class AbkContainerRealFileTests
{
    [Fact]
    public void Parse_Ps3Sample_FindsOneEaXasStereoSound()
    {
        if (!SampleFileCatalog.AbkSamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.AbkGoalPs3SampleFile);
        Result<AbkFile> result = AbkContainer.Parse(data);

        Assert.True(result.IsSuccess, result.Error);
        AbkSound sound = Assert.Single(result.Value!.Sounds);
        Assert.False(sound.IsDummy);
        Assert.Equal(AbkCodecKind.EaXas, sound.Codec);
        Assert.Equal(44100, sound.SampleRate);
        Assert.Equal(2, sound.Channels);
        Assert.Equal(558867, sound.SampleCount);
    }

    [Fact]
    public void Parse_Goal360Sample_FindsOneXma2MonoSound()
    {
        if (!SampleFileCatalog.AbkSamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.AbkGoal360SampleFile);
        Result<AbkFile> result = AbkContainer.Parse(data);

        Assert.True(result.IsSuccess, result.Error);
        AbkSound sound = Assert.Single(result.Value!.Sounds);
        Assert.False(sound.IsDummy);
        Assert.Equal(AbkCodecKind.Xma2, sound.Codec);
        Assert.Equal(44100, sound.SampleRate);
        Assert.Equal(1, sound.Channels);
        Assert.Equal(554587, sound.SampleCount);
    }

    [Fact]
    public void Decode_Ps3Sample_ProducesNonSilentClipWithoutClipping()
    {
        if (!SampleFileCatalog.AbkSamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.AbkGoalPs3SampleFile);
        AbkFile file = AbkContainer.Parse(data).Value!;
        AbkSound sound = file.Sounds[0];

        Result<AbkPcmAudio> decoded = AbkAudioDecoder.Decode(file, sound, ffmpegExePath: null);

        Assert.True(decoded.IsSuccess, decoded.Error);
        AssertPlausibleAudio(decoded.Value!, sound.SampleCount);
    }

    [Fact]
    public void Decode_Goal360Sample_ProducesNonSilentClipWithoutClipping()
    {
        if (!SampleFileCatalog.AbkSamplesAvailable || !SampleFileCatalog.AbkFfmpegAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.AbkGoal360SampleFile);
        AbkFile file = AbkContainer.Parse(data).Value!;
        AbkSound sound = file.Sounds[0];

        Result<AbkPcmAudio> decoded = AbkAudioDecoder.Decode(file, sound, SampleFileCatalog.AbkFfmpegExePath);

        Assert.True(decoded.IsSuccess, decoded.Error);
        AssertPlausibleAudio(decoded.Value!, sound.SampleCount);
    }

    /// <summary>A regression guard, not a correctness proof: catches "decoder throws / returns
    /// silence / clips constantly" but can't tell a clean decode from a differently-wrong one.
    /// The two decoders above were verified by ear against real hardware-recorded clips first.</summary>
    private static void AssertPlausibleAudio(AbkPcmAudio audio, int expectedSampleCount)
    {
        Assert.Equal(expectedSampleCount * audio.Channels * 2, audio.Pcm16.Length);

        long sumAbs = 0;
        int clipped = 0;
        int sampleValueCount = audio.Pcm16.Length / 2;
        for (int i = 0; i < audio.Pcm16.Length; i += 2)
        {
            short s = (short)(audio.Pcm16[i] | (audio.Pcm16[i + 1] << 8));
            sumAbs += Math.Abs((int)s);
            if (s >= 32760 || s <= -32760)
            {
                clipped++;
            }
        }

        double meanAbs = (double)sumAbs / sampleValueCount;
        double clippedFraction = (double)clipped / sampleValueCount;

        Assert.True(meanAbs > 50, $"Decoded audio is implausibly quiet (mean |sample| = {meanAbs:F1}); likely silence, not a real decode.");
        Assert.True(clippedFraction < 0.01, $"Decoded audio clips {clippedFraction:P1} of samples; a clean decode of this clip should barely clip at all.");
    }
}
