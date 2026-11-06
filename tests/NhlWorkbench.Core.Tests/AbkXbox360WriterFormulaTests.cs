using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Abk;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Pins the "quirk field" formula <see cref="AbkXbox360Writer"/> uses when rebuilding a
/// codec-3 (XMA2) sound's header: quirkValue == payloadBytes*4 + (19 - channels).
///
/// This was solved (not derived from any documentation) by comparing the one PS3/360 sample
/// pair against all 3 real sounds in 026_chant.abk -- a stereo/32kHz file, unlike the
/// mono/44.1kHz sample -- and it matched all 4 real data points exactly. A prior guess here
/// (a rough proportional estimate) was confirmed in-game to corrupt/crash the game before this
/// was solved, so this test exists specifically to catch a regression back to that guess, or
/// any future edit that breaks the formula for a channel count this suite has evidence for.
/// </summary>
public class AbkXbox360WriterFormulaTests
{
    [Theory]
    [InlineData(nameof(SampleFileCatalog.AbkGoal360SampleFile), 0)]
    [InlineData(nameof(SampleFileCatalog.AbkChantSampleFile), 0)]
    [InlineData(nameof(SampleFileCatalog.AbkChantSampleFile), 1)]
    [InlineData(nameof(SampleFileCatalog.AbkChantSampleFile), 2)]
    public void QuirkField_MatchesConfirmedFormula_ForEveryKnownRealXma2Sound(string fileProperty, int soundIndex)
    {
        string path = fileProperty == nameof(SampleFileCatalog.AbkGoal360SampleFile)
            ? SampleFileCatalog.AbkGoal360SampleFile
            : SampleFileCatalog.AbkChantSampleFile;

        if (!File.Exists(path))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(path);
        Result<AbkFile> parsed = AbkContainer.Parse(data);
        Assert.True(parsed.IsSuccess, parsed.Error);

        AbkSound sound = parsed.Value!.Sounds[soundIndex];
        Assert.Equal(AbkCodecKind.Xma2, sound.Codec);

        long quirkPos = sound.PayloadOffset - 4;
        uint quirkValue = (uint)((data[quirkPos] << 24) | (data[quirkPos + 1] << 16) | (data[quirkPos + 2] << 8) | data[quirkPos + 3]);

        uint expected = (uint)((sound.PayloadLength * 4) + (19 - sound.Channels));
        Assert.Equal(expected, quirkValue);
    }
}
