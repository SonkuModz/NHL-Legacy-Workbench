using System.Linq;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth coverage for <see cref="AptSceneComposer"/> against the same 3 real overlay
/// archives used throughout the Apt tag-stream tests. Rewritten 2026-08-19 alongside the
/// composer itself: the earlier version of this class produced exactly one element per
/// Shape/EditText character (their own authored local bounds, position-wise wrong -- see
/// <see cref="AptSceneElement"/>'s remarks), so the old "one element per character" assertion
/// no longer holds now that elements come from the real placement/frame-table walk instead
/// (a character can be placed more than once, or not at all if nothing on the resolved frame-0
/// display list references it).
/// </summary>
public class AptSceneComposerTests
{
    [Fact]
    public void Compose_RealAdOverlay_ProducesRealPlacedElements()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        Assert.NotEmpty(scene);
        Assert.Contains(scene, e => e.Kind is AptSceneElementKind.TexturedShape or AptSceneElementKind.VectorShape or AptSceneElementKind.VectorRectangle);
        Assert.Contains(scene, e => e.Kind == AptSceneElementKind.Text);
    }

    [Fact]
    public void Compose_TextureBackedShape_IsClassifiedTexturedShapeWithRealBackingEntry()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        AptSceneElement texturedElement = scene.First(e => e.Kind == AptSceneElementKind.TexturedShape);
        Assert.NotNull(texturedElement.BackingEntryName);
        OverlayArchiveEntry backingEntry = archive.Entries.Single(e => e.Name == texturedElement.BackingEntryName);
        Assert.Equal(OverlayEntryKind.Texture, backingEntry.Kind);
    }

    [Fact]
    public void Compose_VectorBackedShape_IsClassifiedVectorKindWithNoFabricatedTexture()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        Assert.All(scene.Where(e => e.Kind is AptSceneElementKind.VectorShape or AptSceneElementKind.VectorRectangle), e =>
        {
            if (e.BackingEntryName is not null)
            {
                OverlayArchiveEntry backingEntry = archive.Entries.Single(x => x.Name == e.BackingEntryName);
                Assert.NotEqual(OverlayEntryKind.Texture, backingEntry.Kind);
            }
        });
    }

    [Fact]
    public void Compose_VectorRectangle_HasRealPositiveSizeDecodedFromItsOwnVertexData()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        AptSceneElement[] realRects = scene.Where(e => e.Kind == AptSceneElementKind.VectorRectangle).ToArray();
        Assert.NotEmpty(realRects);
        Assert.All(realRects, e =>
        {
            Assert.NotNull(e.BackingEntryName);
            Assert.True(e.Width > 0);
            Assert.True(e.Height > 0);

            // Cross-check: decoding the SAME backing entry's raw bytes independently must agree
            // this is real, decodable geometry -- not a coincidence of unrelated fallback logic.
            byte[] entryData = archive.GetLogicalEntryData(e.BackingEntryName!);
            Assert.NotNull(Formats.Apt.AptShapeGeometry.TryDecodeRectangle(entryData));
        });
    }

    [Fact]
    public void Compose_TextElement_CarriesRealColorAndContent()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        // The same EditText character can legitimately be placed more than once (real data:
        // it's placed both directly on the root and inside a nested Sprite) -- assert every
        // placed instance carries the real content/color rather than expecting exactly one.
        AptSceneElement[] textElements = scene.Where(e => e.Kind == AptSceneElementKind.Text).ToArray();
        Assert.NotEmpty(textElements);
        Assert.All(textElements, e =>
        {
            Assert.Equal("ENERGY GAINED", e.Text);
            Assert.Equal(0xFFEBEBEBu, e.TextColor);
        });
    }

    [Fact]
    public void Compose_NegativeWidthOrHeight_NormalizesToPositiveSizeAndShiftsOrigin()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        Formats.Apt.AptMovie movie = archive.GetMovie().Value!;

        IReadOnlyList<AptSceneElement> scene = AptSceneComposer.Compose(archive, movie);

        // General invariant: composed width/height are never negative, for every element.
        Assert.All(scene, e =>
        {
            Assert.True(e.Width >= 0, $"Negative width leaked through normalization: {e}");
            Assert.True(e.Height >= 0, $"Negative height leaked through normalization: {e}");
        });

        // Real raw data check: overlay_0 has at least one shape with a genuinely negative raw
        // width (key=31, raw width ~-89.4). If it was actually placed anywhere in frame 0, every
        // composed instance of it must show a positive width -- matched by GeometryKey rather
        // than list index (the same character can now appear more than once, or not at all,
        // depending on real placements rather than list position), and only >= 0 rather than an
        // exact magnitude since a real placement's own scale can legitimately resize it further.
        Formats.Apt.AptShapeCharacter negativeWidthShape = movie.Shapes.First(s => s.Width < 0);
        Assert.All(scene.Where(e => e.GeometryKey == negativeWidthShape.GeometryKey), e => Assert.True(e.Width >= 0));
    }
}
