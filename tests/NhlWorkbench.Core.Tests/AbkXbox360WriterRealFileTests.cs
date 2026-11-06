using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Round-trips the Xbox 360 (XMA2) write pipeline against the real sample: replace sound 0
/// with our own decoded copy of itself, then parse and decode the result with this project's
/// own reader/decoder and check it still looks like real, clean audio. This is a from-scratch
/// byte-relocation implementation (no existing tool understands codec 3 in this container),
/// ported from the PS3 tool's decompiled algorithm -- this test is the only thing standing
/// between "compiles" and "actually produces a file the parser can still read back."
/// </summary>
public class AbkXbox360WriterRealFileTests
{
    private static bool ToolsAvailable =>
        SampleFileCatalog.AbkSamplesAvailable
        && SampleFileCatalog.AbkFfmpegAvailable
        && SampleFileCatalog.AbkXma2EncodeAvailable;

    [Fact]
    public void ReplaceSound_Goal360Sample_WithItsOwnDecodedAudio_ProducesAStillValidReadableBank()
    {
        if (!ToolsAvailable)
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "AbkXbox360WriterTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] originalData = File.ReadAllBytes(SampleFileCatalog.AbkGoal360SampleFile);
            AbkFile originalFile = AbkContainer.Parse(originalData).Value!;
            AbkSound originalSound = originalFile.Sounds[0];

            Result<AbkPcmAudio> decoded = AbkAudioDecoder.Decode(originalFile, originalSound, SampleFileCatalog.AbkFfmpegExePath);
            Assert.True(decoded.IsSuccess, decoded.Error);

            string sourceWav = Path.Combine(tempDir, "source.wav");
            File.WriteAllBytes(sourceWav, WavFile.Write(decoded.Value!));

            string outputAbk = Path.Combine(tempDir, "024_goal360.abk");

            Result<string> writeResult = AbkXbox360Writer.ReplaceSound(
                SampleFileCatalog.AbkGoal360SampleFile,
                sourceWav,
                originalSound.Index,
                outputAbk,
                SampleFileCatalog.AbkFfmpegExePath,
                SampleFileCatalog.AbkXma2EncodeExePath);

            Assert.True(writeResult.IsSuccess, writeResult.Error);
            Assert.True(File.Exists(outputAbk));

            byte[] rewrittenData = File.ReadAllBytes(outputAbk);
            Result<AbkFile> reparsed = AbkContainer.Parse(rewrittenData);
            Assert.True(reparsed.IsSuccess, reparsed.Error);

            AbkSound rewrittenSound = reparsed.Value!.Sounds[0];
            Assert.Equal(AbkCodecKind.Xma2, rewrittenSound.Codec);
            Assert.Equal(1, rewrittenSound.Channels);
            Assert.Equal(44100, rewrittenSound.SampleRate);

            Result<AbkPcmAudio> redecoded = AbkAudioDecoder.Decode(reparsed.Value!, rewrittenSound, SampleFileCatalog.AbkFfmpegExePath);
            Assert.True(redecoded.IsSuccess, redecoded.Error);

            long sumAbs = 0;
            int n = redecoded.Value!.Pcm16.Length / 2;
            for (int i = 0; i < redecoded.Value.Pcm16.Length; i += 2)
            {
                short s = (short)(redecoded.Value.Pcm16[i] | (redecoded.Value.Pcm16[i + 1] << 8));
                sumAbs += Math.Abs((int)s);
            }

            double meanAbs = (double)sumAbs / n;
            Assert.True(meanAbs > 50, $"Re-encoded+redecoded audio is implausibly quiet (mean |sample| = {meanAbs:F1}).");
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
}
