using NhlWorkbench.Core.Formats.Abk;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class SoundShapeTests
{
    private const int Rate = 10;

    /// <summary>Mono samples, each frame holding the given value.</summary>
    private static byte[] Pcm(params short[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static short[] Samples(byte[] pcm) => Enumerable.Range(0, pcm.Length / 2).Select(i => BitConverter.ToInt16(pcm, i * 2)).ToArray();

    [Fact]
    public void DoingNothingKeepsTheSamples()
    {
        byte[] pcm = Pcm(1, -2, 3);
        Assert.True(new SoundShape().DoesNothing);
        Assert.Equal(pcm, new SoundShape().Apply(pcm, Rate, 1));
        Assert.False(new SoundShape(VolumeDb: 1).DoesNothing);
    }

    [Fact]
    public void TrimmingKeepsTheRightPart()
    {
        byte[] pcm = Pcm(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal([3, 4, 5, 6, 7, 8, 9], Samples(new SoundShape(StartSeconds: 0.3).Apply(pcm, Rate, 1)));
        Assert.Equal([2, 3, 4], Samples(new SoundShape(StartSeconds: 0.2, LengthSeconds: 0.3).Apply(pcm, Rate, 1)));
        Assert.Equal([8, 9], Samples(new SoundShape(StartSeconds: 0.8, LengthSeconds: 50).Apply(pcm, Rate, 1)));
        Assert.Empty(new SoundShape(StartSeconds: 50).Apply(pcm, Rate, 1));
    }

    [Fact]
    public void StereoIsTrimmedByWholeFrames()
    {
        byte[] pcm = Pcm(1, 2, 3, 4, 5, 6);
        Assert.Equal([3, 4, 5, 6], Samples(new SoundShape(StartSeconds: 0.1).Apply(pcm, Rate, 2)));
    }

    [Fact]
    public void VolumeChangesAndHoldsAtTheLimit()
    {
        double six = 20 * Math.Log10(2);
        Assert.Equal([200, -200], Samples(new SoundShape(VolumeDb: six).Apply(Pcm(100, -100), Rate, 1)));
        Assert.Equal([50, -50], Samples(new SoundShape(VolumeDb: -six).Apply(Pcm(100, -100), Rate, 1)));
        Assert.Equal([short.MaxValue, short.MinValue], Samples(new SoundShape(VolumeDb: six).Apply(Pcm(30000, -30000), Rate, 1)));
    }

    [Fact]
    public void FadesReachSilenceAtTheEnds()
    {
        short[] flat = Enumerable.Repeat((short)1000, 10).ToArray();
        short[] fadedIn = Samples(new SoundShape(FadeInSeconds: 0.5).Apply(Pcm(flat), Rate, 1));
        Assert.Equal(0, fadedIn[0]);
        Assert.True(fadedIn[1] > 0 && fadedIn[1] < fadedIn[4]);
        Assert.Equal(1000, fadedIn[9]);

        short[] fadedOut = Samples(new SoundShape(FadeOutSeconds: 0.5).Apply(Pcm(flat), Rate, 1));
        Assert.Equal(1000, fadedOut[0]);
        Assert.Equal(0, fadedOut[9]);
        Assert.True(fadedOut[5] > fadedOut[8]);
    }

    [Fact]
    public void TheOriginalIsLeftAlone()
    {
        byte[] pcm = Pcm(100, 200);
        new SoundShape(VolumeDb: 6).Apply(pcm, Rate, 1);
        Assert.Equal(Pcm(100, 200), pcm);
    }
}
