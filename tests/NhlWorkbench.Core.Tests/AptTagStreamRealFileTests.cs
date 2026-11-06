using System.Linq;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth coverage for the tag-stream decoding added 2026-08-19, after a fellow
/// modder shared community research on the same EA APT format from NCAA Football 14
/// (References/aptresearch/). The character-type numbering (Shape=1, EditText=2,
/// Font=3, Sprite=5, Image=7, Movie=9) came from that research; every field-level
/// layout asserted below was independently re-derived and verified against real NHL
/// bytes across all 3 sample archives, not copied blind -- see each type's own
/// remarks in src/NhlWorkbench.Core/Formats/Apt/ for the provenance of each fact.
/// </summary>
public class AptTagStreamRealFileTests
{
    [Fact]
    public void Stage_EveryRealMovie_Reports1280x720()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        foreach (string path in new[]
                 {
                     SampleFileCatalog.OverlaySampleFile,
                     SampleFileCatalog.OverlaySampleFileWithTwoImports,
                     SampleFileCatalog.OverlaySampleFileLeaf,
                 })
        {
            byte[] data = File.ReadAllBytes(path);
            OverlayArchive archive = OverlayArchive.Open(data, path).Value!;
            AptMovie movie = archive.GetMovie().Value!;

            AptMovieCharacter? stage = movie.Stage;
            Assert.NotNull(stage);
            Assert.Equal(1280, stage!.StageWidth);
            Assert.Equal(720, stage.StageHeight);
        }
    }

    [Fact]
    public void Tags_EveryRecordInEveryRealMovie_CarriesTheMagicSentinel()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        int totalTags = 0;
        foreach (string path in new[]
                 {
                     SampleFileCatalog.OverlaySampleFile,
                     SampleFileCatalog.OverlaySampleFileWithTwoImports,
                     SampleFileCatalog.OverlaySampleFileLeaf,
                 })
        {
            byte[] data = File.ReadAllBytes(path);
            OverlayArchive archive = OverlayArchive.Open(data, path).Value!;
            AptMovie movie = archive.GetMovie().Value!;

            Assert.NotEmpty(movie.Tags);
            totalTags += movie.Tags.Count;
            foreach (AptTagRecord tag in movie.Tags)
            {
                uint magicField = Utilities.BigEndianReader.ReadUInt32BEAt(tag.RawData, 4);
                Assert.Equal(AptTagRecord.Magic, magicField);
            }
        }

        // Pins the exact corpus size this decoder was validated against, so a future
        // regression (e.g. the sequential walk stopping early) shows up as a count
        // change here instead of silently parsing fewer tags. Raised from an earlier
        // pin of 127: that count came from an offset-table-based reader later found to
        // undercount by roughly two thirds (see AptTagStreamReader's remarks) --
        // 160+65+21 is the real, sequentially-walked total across the same 3 files.
        Assert.Equal(246, totalTags);
    }

    [Fact]
    public void ShapeGeometryKey_EveryShapeInEveryRealMovie_ResolvesToARealArchiveEntry()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        int totalShapes = 0, textureBackedShapes = 0;
        foreach (string path in new[]
                 {
                     SampleFileCatalog.OverlaySampleFile,
                     SampleFileCatalog.OverlaySampleFileWithTwoImports,
                     SampleFileCatalog.OverlaySampleFileLeaf,
                 })
        {
            byte[] data = File.ReadAllBytes(path);
            OverlayArchive archive = OverlayArchive.Open(data, path).Value!;
            AptMovie movie = archive.GetMovie().Value!;

            Assert.NotEmpty(movie.Shapes);
            totalShapes += movie.Shapes.Count;
            foreach (AptShapeCharacter shape in movie.Shapes)
            {
                OverlayArchiveEntry? resolved = archive.TryResolveShapeGeometryEntry(shape.GeometryKey);
                Assert.True(resolved is not null, $"{Path.GetFileName(path)}: geometry key {shape.GeometryKey} did not resolve to any real archive entry.");
                Assert.Equal((shape.GeometryKey + 2).ToString(), resolved!.Name);

                // Most Shapes resolve to real vector geometry, but a Shape can validly be a
                // plain image-filled rect instead -- see this method's remarks. Both are
                // legitimate; only "resolves to nothing at all" would be a real failure.
                if (resolved.Kind == OverlayEntryKind.Texture)
                {
                    textureBackedShapes++;
                }
            }
        }

        // Raised from an earlier pin of 33 for the same reason as the tag-count pin above --
        // the sequential walker finds real shapes the old offset-table reader missed.
        Assert.Equal(70, totalShapes);
        Assert.True(textureBackedShapes > 0, "Expected at least one real Shape to resolve to a texture-backed (plain rect fill) entry.");
    }

    [Fact]
    public void EditText_RealAdOverlayText_ResolvesActualStringContent()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;
        AptMovie movie = archive.GetMovie().Value!;

        Assert.Single(movie.EditTexts);
        AptEditTextCharacter text = movie.EditTexts[0];
        Assert.Equal("ENERGY GAINED", text.Text);
        Assert.Equal(0xFF, text.Alpha);
    }

    [Fact]
    public void Font_RealMovies_ResolveARealFontName()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;
        AptMovie movie = archive.GetMovie().Value!;

        Assert.Single(movie.Fonts);
        Assert.Equal("EA HelveticaNeue Con Bold", movie.Fonts[0].FontName);
    }

    [Fact]
    public void ConstantFile_RealFiles_ResolveHeaderFieldsAndAptDataOffsetMatchesTheMoviesOwnFirstTag()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        foreach (string path in new[]
                 {
                     SampleFileCatalog.OverlaySampleFile,
                     SampleFileCatalog.OverlaySampleFileWithTwoImports,
                     SampleFileCatalog.OverlaySampleFileLeaf,
                 })
        {
            byte[] data = File.ReadAllBytes(path);
            OverlayArchive archive = OverlayArchive.Open(data, path).Value!;

            Result<AptConstantFile> constants = archive.GetConstantFile();
            Assert.True(constants.IsSuccess, constants.Error);
            AptConstantFile c = constants.Value!;

            Assert.True(c.ItemCount > 0);
            Assert.Equal(0x20u, c.TablePointer);
            Assert.All(c.Items, item => Assert.Equal(1u, item.Type));
            Assert.Contains(c.Items, i => !string.IsNullOrEmpty(i.ResolvedString));

            // Cross-validates against the movie's OWN independently-derived tag-stream
            // start -- two different byte regions (CONST header field vs. movie's class
            // table arithmetic) agreeing is what makes this a confirmed fact, not a guess.
            AptMovie movie = archive.GetMovie().Value!;
            Assert.NotEmpty(movie.Tags);
            Assert.Equal((int)c.AptDataOffset, movie.Tags[0].Offset);
        }
    }
}
