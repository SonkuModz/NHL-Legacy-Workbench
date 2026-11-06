using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

public class AptPositionedPreviewBuilderTests
{
    [Fact]
    public void Build_RealMovie_ReturnsOnlyFinalDisplayListElementsWithSensibleKinds()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptPositionedElement> elements = AptPositionedPreviewBuilder.Build(movie.RawData, movie, archive);

        Assert.NotEmpty(elements);
        Assert.Contains(elements, e => e.Kind == AptPositionedElementKind.Container);
        Assert.Contains(elements, e => e.Kind == AptPositionedElementKind.TexturedShape);

        // Ground truth confirmed this session: characterId 59 resolves to instance name "mcAd"
        // and is backed by a real texture. It's on the empirically-frozen list (see
        // AptPlacementEditor's remarks), so it must NOT be marked as responding to Move.
        AptPositionedElement mcAd = Assert.Single(elements, e => e.CharacterId == 59);
        Assert.Equal("mcAd", mcAd.Name);
        Assert.Equal(AptPositionedElementKind.TexturedShape, mcAd.Kind);
        Assert.NotNull(mcAd.TextureEntryName);
        Assert.False(mcAd.RespondsToMove);

        // Ground truth confirmed via direct bytecode disassembly: characterId 89
        // ("mcPeriodBackground") is script-controlled -- must also be marked as NOT responding.
        AptPositionedElement periodBackground = Assert.Single(elements, e => e.CharacterId == 89);
        Assert.False(periodBackground.RespondsToMove);

        // A confirmed static-only piece of chrome (characterId 121, "mcFrameCapRight") DOES
        // respond, proving the flag is targeted, not defaulting to false for everything.
        AptPositionedElement frameCap = Assert.Single(elements, e => e.CharacterId == 121);
        Assert.True(frameCap.RespondsToMove);

        foreach (AptPositionedElement e in elements)
        {
            if (e.Kind == AptPositionedElementKind.TexturedShape)
            {
                Assert.False(string.IsNullOrEmpty(e.TextureEntryName));
            }
            else
            {
                Assert.Null(e.TextureEntryName);
            }
        }
    }
}
