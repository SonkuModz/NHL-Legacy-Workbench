using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Ground-truth coverage for <see cref="AptPlacementEditor"/> against real overlay archives.</summary>
public class AptPlacementEditorTests
{
    [Fact]
    public void RepositionRootPlacements_RealMovie_ShiftsEveryRootPlacementByTheExactDelta()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;
        AptTagRecord movieTag = before.Tags.First(t => t.Type == AptCharacterType.Movie);

        var beforeTranslates = AptFrameTableReader.ReadFrameZeroPlacements(before.RawData, movieTag.Offset)
            .Select(p => (p.CharacterId, p.TranslateX, p.TranslateY))
            .ToList();
        Assert.NotEmpty(beforeTranslates);

        byte[] mutable = (byte[])before.RawData.Clone();
        int patched = AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 50f, deltaY: -30f);
        Assert.True(patched > 0);

        AptMovie after = AptMovieReader.Parse(mutable).Value!;
        var afterTranslates = AptFrameTableReader.ReadFrameZeroPlacements(after.RawData, movieTag.Offset)
            .Select(p => (p.CharacterId, p.TranslateX, p.TranslateY))
            .ToList();

        Assert.Equal(beforeTranslates.Count, afterTranslates.Count);
        for (int i = 0; i < beforeTranslates.Count; i++)
        {
            Assert.Equal(beforeTranslates[i].CharacterId, afterTranslates[i].CharacterId);
            Assert.Equal(beforeTranslates[i].TranslateX + 50f, afterTranslates[i].TranslateX, 3);
            Assert.Equal(beforeTranslates[i].TranslateY - 30f, afterTranslates[i].TranslateY, 3);
        }
    }

    [Fact]
    public void RepositionRootPlacements_RealMovie_LeavesEverythingElseUntouched()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;

        byte[] mutable = (byte[])before.RawData.Clone();
        AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 50f, deltaY: -30f);

        AptMovie after = AptMovieReader.Parse(mutable).Value!;
        Assert.Equal(before.Tags.Count, after.Tags.Count);
        Assert.Equal(before.Stage?.StageWidth, after.Stage?.StageWidth);
        Assert.Equal(before.Stage?.StageHeight, after.Stage?.StageHeight);
        Assert.Equal(before.Shapes.Count, after.Shapes.Count);
        Assert.Equal(before.EditTexts.Count, after.EditTexts.Count);
    }

    [Fact]
    public void RepositionRootPlacements_MutatedBytes_SurviveARealRefPackRoundTrip()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;

        byte[] mutable = (byte[])before.RawData.Clone();
        AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 50f, deltaY: -30f);

        var compressed = RefPack.Compress(mutable);
        Assert.True(compressed.IsSuccess);
        var decompressed = RefPack.Decompress(compressed.Value!);
        Assert.True(decompressed.IsSuccess);
        Assert.Equal(mutable, decompressed.Value!);
    }

    [Fact]
    public void RepositionRootPlacements_ZeroDelta_IsANoOpOnTranslateValues()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;
        AptMovie before = archive.GetMovie().Value!;

        byte[] mutable = (byte[])before.RawData.Clone();
        int patched = AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 0f, deltaY: 0f);

        Assert.True(patched > 0);
        Assert.Equal(before.RawData, mutable);
    }

    [Fact]
    public void RepositionRootPlacements_ContainersOnly_ShiftsSpriteAndMoviePlacementsButLeavesShapeAndEditTextInPlace()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;
        AptTagRecord movieTag = before.Tags.First(t => t.Type == AptCharacterType.Movie);

        var beforePlacements = AptFrameTableReader.ReadFrameZeroPlacements(before.RawData, movieTag.Offset);
        Assert.Contains(beforePlacements, p => before.ResolveCharacterTagIndex(p.CharacterId) is int i && i >= 0 && before.Tags[i].Type == AptCharacterType.Shape);
        Assert.Contains(beforePlacements, p => before.ResolveCharacterTagIndex(p.CharacterId) is int i && i >= 0 && before.Tags[i].Type == AptCharacterType.Sprite);

        byte[] mutable = (byte[])before.RawData.Clone();
        int patched = AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 20f, deltaY: 0f, containersOnly: true);
        Assert.True(patched > 0);

        AptMovie after = AptMovieReader.Parse(mutable).Value!;
        var afterPlacements = AptFrameTableReader.ReadFrameZeroPlacements(after.RawData, movieTag.Offset);
        Assert.Equal(beforePlacements.Count, afterPlacements.Count);

        for (int i = 0; i < beforePlacements.Count; i++)
        {
            int idx = before.ResolveCharacterTagIndex(beforePlacements[i].CharacterId);
            bool isContainer = idx >= 0 && before.Tags[idx].Type is AptCharacterType.Sprite or AptCharacterType.Movie;
            float expectedX = beforePlacements[i].TranslateX + (isContainer ? 20f : 0f);
            Assert.Equal(expectedX, afterPlacements[i].TranslateX, 2);
            Assert.Equal(beforePlacements[i].TranslateY, afterPlacements[i].TranslateY, 2);
        }
    }

    [Fact]
    public void RepositionRootPlacements_FinalDisplayListOnly_ShiftsExactlyTheDepthReplayWinnersAndLeavesSupersededRecordsUntouched()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;
        AptTagRecord movieTag = before.Tags.First(t => t.Type == AptCharacterType.Movie);

        // Ground truth: character 58 is superseded (never really on screen) while its apparent
        // tile partner, character 59, survives -- confirmed via scratch depth-replay tooling
        // against this same real file (2026-08-20). If this ever stops being true (e.g. the
        // sample file is swapped), the test still exercises the real invariant below and would
        // just need its illustrative asserts updated.
        var beforePlacements = AptFrameTableReader.ReadFrameZeroPlacements(before.RawData, movieTag.Offset);
        Assert.Contains(beforePlacements, p => p.CharacterId == 58);
        Assert.Contains(beforePlacements, p => p.CharacterId == 59);

        byte[] mutable = (byte[])before.RawData.Clone();
        int patched = AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 20f, deltaY: 0f, finalDisplayListOnly: true);
        Assert.True(patched > 0);
        Assert.True(patched < beforePlacements.Count, "finalDisplayListOnly should patch a strict subset of the naive placement count (most raw records are superseded).");

        AptMovie after = AptMovieReader.Parse(mutable).Value!;
        var afterPlacements = AptFrameTableReader.ReadFrameZeroPlacements(after.RawData, movieTag.Offset);
        Assert.Equal(beforePlacements.Count, afterPlacements.Count);

        var beforeCid58 = beforePlacements.First(p => p.CharacterId == 58);
        var afterCid58 = afterPlacements.First(p => p.CharacterId == 58);
        Assert.Equal(beforeCid58.TranslateX, afterCid58.TranslateX, 2); // superseded -- must NOT move

        // cid=59 is the real depth-replay survivor at that depth, but its own instance name
        // resolves to "mcAd" -- a separate advertisement slot, not scorebug chrome. Two controlled
        // live tests (2026-08-20/21, one variable changed each time) confirmed it must be excluded
        // from the shift: moving it drags its ad-placeholder graphic into the bar; freezing it
        // fixes that (see NonChromeInstanceNames's remarks for the full history, including a
        // separate delta-size-dependent collision this exclusion does NOT yet resolve).
        var beforeCid59 = beforePlacements.First(p => p.CharacterId == 59);
        var afterCid59 = afterPlacements.First(p => p.CharacterId == 59);
        Assert.Equal(beforeCid59.TranslateX, afterCid59.TranslateX, 2); // "mcAd" -- must NOT move

        // A confirmed real piece of bar chrome (cid=121, "mcFrameCapRight") DOES move, proving the
        // exclusion is targeted at "mcAd" specifically, not accidentally suppressing everything.
        var beforeCid121 = beforePlacements.First(p => p.CharacterId == 121);
        var afterCid121 = afterPlacements.First(p => p.CharacterId == 121);
        Assert.Equal(beforeCid121.TranslateX + 20f, afterCid121.TranslateX, 2);

        // cid=149 was briefly excluded on the theory it carries the EA badge, but a live test
        // disproved that (the badge still drifted with 149 frozen) -- reverted, so it moves
        // uniformly like everything else in the final set again (see NonChromeCharacterIds's
        // remarks for the full history). cid=149 appears more than once in the raw array (some
        // occurrences stale/superseded per the depth-replay findings elsewhere in this file), so
        // check that AT LEAST ONE real occurrence shifted by +20, rather than assuming .First()
        // picks a depth-replay winner.
        var beforeCid149Xs = beforePlacements.Where(p => p.CharacterId == 149).Select(p => p.TranslateX).ToList();
        var afterCid149Xs = afterPlacements.Where(p => p.CharacterId == 149).Select(p => p.TranslateX).ToList();
        Assert.Contains(beforeCid149Xs, x => afterCid149Xs.Any(ax => Math.Abs(ax - (x + 20f)) < 0.01f));
    }

    [Fact]
    public void RepositionRootPlacements_FinalDisplayListOnly_FreezesTheConfirmedScriptControlledTier()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie before = archive.GetMovie().Value!;
        AptTagRecord movieTag = before.Tags.First(t => t.Type == AptCharacterType.Movie);
        var beforePlacements = AptFrameTableReader.ReadFrameZeroPlacements(before.RawData, movieTag.Offset);

        byte[] mutable = (byte[])before.RawData.Clone();
        AptPlacementEditor.RepositionRootPlacements(mutable, before, deltaX: 20f, deltaY: 0f, finalDisplayListOnly: true);

        AptMovie after = AptMovieReader.Parse(mutable).Value!;
        var afterPlacements = AptFrameTableReader.ReadFrameZeroPlacements(after.RawData, movieTag.Offset);

        // Ground truth confirmed 2026-08-22 by disassembling this file's own ActionScript: cid=89
        // ("mcPeriodBackground") has its real on-screen `_x` set by `gAnimator.Animate(...)` at
        // runtime in both PositionPanels and RepositionPanels -- a static translateX edit to it is
        // silently overwritten on load, so it must stay frozen (not because it's superseded or an
        // ad slot, but because moving it would be pointless and would skew anchor-snap bounds).
        var beforeCid89 = beforePlacements.First(p => p.CharacterId == 89);
        var afterCid89 = afterPlacements.First(p => p.CharacterId == 89);
        Assert.Equal(beforeCid89.TranslateX, afterCid89.TranslateX, 2);

        // cid=121 ("mcFrameCapRight") is confirmed static-only chrome -- it DOES move, proving the
        // exclusion is targeted at the script-controlled tier specifically.
        var beforeCid121 = beforePlacements.First(p => p.CharacterId == 121);
        var afterCid121 = afterPlacements.First(p => p.CharacterId == 121);
        Assert.Equal(beforeCid121.TranslateX + 20f, afterCid121.TranslateX, 2);
    }

    [Theory]
    [InlineData(AptAnchorPosition.TopLeft)]
    [InlineData(AptAnchorPosition.TopCenter)]
    [InlineData(AptAnchorPosition.TopRight)]
    [InlineData(AptAnchorPosition.MiddleLeft)]
    [InlineData(AptAnchorPosition.Center)]
    [InlineData(AptAnchorPosition.MiddleRight)]
    [InlineData(AptAnchorPosition.BottomLeft)]
    [InlineData(AptAnchorPosition.BottomCenter)]
    [InlineData(AptAnchorPosition.BottomRight)]
    public void ComputeAnchorDelta_RealMovie_LandsTheBoundsExactlyAtTheRequestedStageEdgeOrCenter(AptAnchorPosition anchor)
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        AptMovie movie = archive.GetMovie().Value!;

        var bounds = AptPlacementEditor.ComputeRootPlacementBounds(movie.RawData, movie);
        Assert.NotNull(bounds);

        const float stageWidth = 1280f, stageHeight = 720f;
        (float deltaX, float deltaY) = AptPlacementEditor.ComputeAnchorDelta(bounds!.Value, stageWidth, stageHeight, anchor);

        float newMinX = bounds.Value.MinX + deltaX, newMaxX = bounds.Value.MaxX + deltaX;
        float newMinY = bounds.Value.MinY + deltaY, newMaxY = bounds.Value.MaxY + deltaY;
        float newCenterX = (newMinX + newMaxX) / 2f, newCenterY = (newMinY + newMaxY) / 2f;

        switch (anchor)
        {
            case AptAnchorPosition.TopLeft: Assert.Equal(0f, newMinX, 2); Assert.Equal(0f, newMinY, 2); break;
            case AptAnchorPosition.TopCenter: Assert.Equal(stageWidth / 2f, newCenterX, 2); Assert.Equal(0f, newMinY, 2); break;
            case AptAnchorPosition.TopRight: Assert.Equal(stageWidth, newMaxX, 2); Assert.Equal(0f, newMinY, 2); break;
            case AptAnchorPosition.MiddleLeft: Assert.Equal(0f, newMinX, 2); Assert.Equal(stageHeight / 2f, newCenterY, 2); break;
            case AptAnchorPosition.Center: Assert.Equal(stageWidth / 2f, newCenterX, 2); Assert.Equal(stageHeight / 2f, newCenterY, 2); break;
            case AptAnchorPosition.MiddleRight: Assert.Equal(stageWidth, newMaxX, 2); Assert.Equal(stageHeight / 2f, newCenterY, 2); break;
            case AptAnchorPosition.BottomLeft: Assert.Equal(0f, newMinX, 2); Assert.Equal(stageHeight, newMaxY, 2); break;
            case AptAnchorPosition.BottomCenter: Assert.Equal(stageWidth / 2f, newCenterX, 2); Assert.Equal(stageHeight, newMaxY, 2); break;
            case AptAnchorPosition.BottomRight: Assert.Equal(stageWidth, newMaxX, 2); Assert.Equal(stageHeight, newMaxY, 2); break;
        }
    }

    [Fact]
    public void ComputeRootPlacementBounds_NoRealPlacements_ReturnsNull()
    {
        // A minimal, valid-but-empty movie has no root placements to measure.
        var emptyMovie = new AptMovie
        {
            VersionString = "Apt Data:1:7:4",
            HeaderTagByte = 0x1A,
            ClassReferences = Array.Empty<AptClassReference>(),
            AllStrings = Array.Empty<(int, string)>(),
            Tags = Array.Empty<AptTagRecord>(),
            RawData = Array.Empty<byte>(),
        };

        Assert.Null(AptPlacementEditor.ComputeRootPlacementBounds(Array.Empty<byte>(), emptyMovie));
    }
}
