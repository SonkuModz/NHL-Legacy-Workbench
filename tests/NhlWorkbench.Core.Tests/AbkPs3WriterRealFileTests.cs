using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Round-trips the PS3 write pipeline (ffmpeg preprocessing + the user's own ABK_insert.exe)
/// against the real sample: replace sound 0 with our own decoded copy of itself, then parse
/// and decode the result with this project's own reader/decoder and check it still looks like
/// real, clean audio. Lossy re-encoding means it won't be byte-identical to the original --
/// this is a pipeline-wiring guard (did the tool run, did the offsets/relocation still parse),
/// not a bit-exactness check.
/// </summary>
public class AbkPs3WriterRealFileTests
{
    private static bool ToolsAvailable =>
        SampleFileCatalog.AbkSamplesAvailable
        && SampleFileCatalog.AbkFfmpegAvailable
        && File.Exists(SampleFileCatalog.AbkInsertExePath);

    [Fact]
    public void ReplaceSound_Ps3Sample_WithItsOwnDecodedAudio_ProducesAStillValidReadableBank()
    {
        if (!ToolsAvailable)
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "AbkPs3WriterTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] originalData = File.ReadAllBytes(SampleFileCatalog.AbkGoalPs3SampleFile);
            AbkFile originalFile = AbkContainer.Parse(originalData).Value!;
            AbkSound originalSound = originalFile.Sounds[0];

            Result<AbkPcmAudio> decoded = AbkAudioDecoder.Decode(originalFile, originalSound, ffmpegExePath: null);
            Assert.True(decoded.IsSuccess, decoded.Error);

            string sourceWav = Path.Combine(tempDir, "source.wav");
            File.WriteAllBytes(sourceWav, WavFile.Write(decoded.Value!));

            string outputAbk = Path.Combine(tempDir, "024_goalPS3.abk");

            Result<string> writeResult = AbkPs3Writer.ReplaceSound(
                SampleFileCatalog.AbkGoalPs3SampleFile,
                sourceWav,
                originalSound.Index,
                outputAbk,
                SampleFileCatalog.AbkFfmpegExePath,
                SampleFileCatalog.AbkInsertExePath);

            Assert.True(writeResult.IsSuccess, writeResult.Error);
            Assert.True(File.Exists(outputAbk));

            byte[] rewrittenData = File.ReadAllBytes(outputAbk);
            Result<AbkFile> reparsed = AbkContainer.Parse(rewrittenData);
            Assert.True(reparsed.IsSuccess, reparsed.Error);

            AbkSound rewrittenSound = reparsed.Value!.Sounds[0];
            Assert.Equal(AbkCodecKind.EaXas, rewrittenSound.Codec);
            Assert.Equal(2, rewrittenSound.Channels);

            Result<AbkPcmAudio> redecoded = AbkAudioDecoder.Decode(reparsed.Value!, rewrittenSound, ffmpegExePath: null);
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
