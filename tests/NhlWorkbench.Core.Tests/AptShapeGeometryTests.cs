using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth coverage for <see cref="AptShapeGeometry"/>, decoded 2026-08-19 from the
/// external NCAA14 community research's documented "fixed six-vertex quad" DAT leaf format
/// (References/aptresearch/APT_GEOMETRY.md and dated discovery entries), then independently
/// verified against real NHL bytes.
/// </summary>
public class AptShapeGeometryTests
{
    [Fact]
    public void TryDecodeRectangle_RealEntrySix_MatchesItsOwnCharacterTableDeclaredSizeExactly()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        // overlay_0's ShapeRecord entry '6' (backing Shape geometryKey=4) is the exact real
        // record this decoder was verified against: two unrelated byte regions -- the movie's
        // own character-table declared Width/Height, and this entry's real vertex geometry --
        // independently agree on the same 2x2 size. That agreement is what makes this a
        // confirmed fact rather than a plausible-looking guess.
        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;
        byte[] entryData = archive.GetLogicalEntryData("6");

        (float X, float Y, float Width, float Height)? rect = AptShapeGeometry.TryDecodeRectangle(entryData);

        Assert.NotNull(rect);
        Assert.Equal(0f, rect!.Value.X);
        Assert.Equal(0f, rect.Value.Y);
        Assert.Equal(2f, rect.Value.Width);
        Assert.Equal(2f, rect.Value.Height);

        AptMovie movie = archive.GetMovie().Value!;
        AptShapeCharacter shapeCharacter = movie.Shapes.Single(s => s.GeometryKey == 4);
        Assert.Equal(2f, shapeCharacter.Width);
        Assert.Equal(2f, shapeCharacter.Height);
    }

    [Fact]
    public void TryDecodeRectangle_EveryRealSingleChildShapeRecord_DecodesToAPositiveSizeRectangle()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        int decoded = 0;
        foreach (string path in new[]
                 {
                     SampleFileCatalog.OverlaySampleFile,
                     SampleFileCatalog.OverlaySampleFileWithTwoImports,
                     SampleFileCatalog.OverlaySampleFileLeaf,
                 })
        {
            byte[] data = File.ReadAllBytes(path);
            OverlayArchive archive = OverlayArchive.Open(data, path).Value!;

            foreach (OverlayArchiveEntry entry in archive.Entries.Where(e => e.Kind == OverlayEntryKind.ShapeRecord))
            {
                byte[] entryData = archive.GetLogicalEntryData(entry.Name);
                (float X, float Y, float Width, float Height)? rect = AptShapeGeometry.TryDecodeRectangle(entryData);
                if (rect is null)
                {
                    continue; // multi-child records are a known, honestly-unhandled case -- see AptShapeGeometry's remarks.
                }

                decoded++;
                Assert.True(rect.Value.Width > 0, $"{Path.GetFileName(path)} entry '{entry.Name}': decoded non-positive width.");
                Assert.True(rect.Value.Height > 0, $"{Path.GetFileName(path)} entry '{entry.Name}': decoded non-positive height.");
            }
        }

        Assert.True(decoded > 0, "Expected at least one real ShapeRecord entry to decode across the sample corpus.");
    }

    [Fact]
    public void TryDecodeRectangle_TooShortOrWrongSelfLength_ReturnsNullRatherThanGuessing()
    {
        Assert.Null(AptShapeGeometry.TryDecodeRectangle(new byte[16]));
        Assert.Null(AptShapeGeometry.TryDecodeRectangle(new byte[288])); // all zero -- self-length field won't match 288
    }
}
