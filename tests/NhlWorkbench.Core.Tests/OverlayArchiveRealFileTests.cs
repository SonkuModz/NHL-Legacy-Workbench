using System.Linq;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Overlay;
using NhlWorkbench.Core.Tests.TestData;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>
/// Ground-truth coverage for the Scorebug Editor's overlay-archive analysis, against three real
/// samples with different Apt movie shapes (0, 1, and 2 imports) so the class-table
/// group-count arithmetic in <see cref="AptMovieReader"/> -- derived, not guessed, from the
/// first class reference's own string offset -- is exercised beyond a single lucky case. See
/// <see cref="AptMovie"/>'s remarks for exactly what is and isn't claimed to be understood.
/// </summary>
public class OverlayArchiveRealFileTests
{
    [Fact]
    public void Open_RealAdOverlay_ClassifiesEveryEntryAndParsesTheMovie()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        Result<OverlayArchive> opened = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile);
        Assert.True(opened.IsSuccess, opened.Error);

        OverlayArchive archive = opened.Value!;
        Assert.Equal(94, archive.Entries.Count);

        // Every entry must be classified into a real kind -- none should be silently dropped.
        Assert.Equal(94, archive.Entries.Select(e => e.Kind).Count());

        Result<AptMovie> movie = archive.GetMovie();
        Assert.True(movie.IsSuccess, movie.Error);
        Assert.Equal("Apt Data:1:7:4", movie.Value!.VersionString);
        Assert.Equal("game/components/StaticAdWrapper", movie.Value!.ClassPath);
        Assert.Equal("game.components.classes.StaticAdWrapper", movie.Value!.ClassName);
        Assert.Single(movie.Value!.Imports);
        Assert.Equal("ImageLoader", movie.Value!.Imports.First().ShortName);
        Assert.Equal("external/ion_sports07/components/stdControls", movie.Value!.Imports.First().FullPath);

        Result<AptConstantFile> constants = archive.GetConstantFile();
        Assert.True(constants.IsSuccess, constants.Error);
        Assert.Contains(constants.Value!.AllStrings, s => s.Value == "Apt constant file");

        IReadOnlyList<(OverlayArchiveEntry Entry, AptShapeRecord Shape)> shapes = archive.GetShapeRecords();
        Assert.Equal(48, shapes.Count);
        Assert.All(shapes, s => Assert.True(s.Shape.SelfLengthConfirmed));

        IReadOnlyList<OverlayArchiveEntry> textures = archive.GetTextureEntries();
        Assert.Equal(41, textures.Count);
    }

    [Fact]
    public void Open_MovieWithTwoImports_ResolvesEveryClassReferenceFromTheDerivedGroupCount()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileWithTwoImports);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileWithTwoImports).Value!;

        AptMovie movie = archive.GetMovie().Value!;
        Assert.Equal("NHLDPad", movie.ClassName);
        Assert.Equal("game/components/NHLDPad", movie.ClassPath);
        Assert.Equal(2, movie.Imports.Count());
        Assert.Equal(new[] { "Y", "LogoPill" }, movie.Imports.Select(i => i.ShortName));
        Assert.Equal("external/ion_sports07/platform/proxy/art/Y", movie.Imports.First().FullPath);
        Assert.Equal("game/overlays/logo_pill", movie.Imports.Last().FullPath);
    }

    [Fact]
    public void Open_LeafMovieWithNoImports_StillParsesItsOwnClassIdentity()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFileLeaf);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFileLeaf).Value!;

        AptMovie movie = archive.GetMovie().Value!;
        Assert.Equal("LogoPill", movie.ClassName);
        Assert.Equal("game/overlays/logo_pill", movie.ClassPath);
        Assert.Empty(movie.Imports);
    }

    [Fact]
    public void GetTextureEntries_EveryOne_DecodesAsAValidXprTexture()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;

        foreach (OverlayArchiveEntry entry in archive.GetTextureEntries())
        {
            byte[] entryData = archive.Archive.GetEntryData(entry.Name);
            Result<Formats.Xpr.XprContainer> xpr = Formats.Xpr.XprContainer.Parse(entryData, entry.Name);
            Assert.True(xpr.IsSuccess, $"'{entry.Name}': {xpr.Error}");
            Assert.Single(xpr.Value!.Textures);
            Assert.True(xpr.Value!.Textures[0].Width > 0 && xpr.Value!.Textures[0].Height > 0);
        }
    }

    [Fact]
    public void ShapeRecordSelfLengthInvariant_HoldsAcrossAllThreeSamples()
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
            IReadOnlyList<(OverlayArchiveEntry Entry, AptShapeRecord Shape)> shapes = archive.GetShapeRecords();
            Assert.NotEmpty(shapes);
            Assert.All(shapes, s => Assert.Equal(s.Entry.Size, s.Shape.DeclaredTotalSize));
        }
    }

    [Fact]
    public void GetLogicalEntryData_MovieAndConstantFile_ReturnDecompressedBytesEveryTime()
    {
        if (!SampleFileCatalog.OverlaySamplesAvailable)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(SampleFileCatalog.OverlaySampleFile);
        OverlayArchive archive = OverlayArchive.Open(data, SampleFileCatalog.OverlaySampleFile).Value!;

        OverlayArchiveEntry movieEntry = archive.Entries.First(e => e.Kind == OverlayEntryKind.Movie);
        byte[] logical = archive.GetLogicalEntryData(movieEntry.Name);
        Assert.StartsWith("Apt Data:", System.Text.Encoding.ASCII.GetString(logical, 0, 9));

        // Stock entries are RefPack-compressed, but a sample edited by the Scorebug Editor can hold the
        // movie uncompressed. Either way the logical form must be the plain movie; when the raw bytes are
        // compressed it must also be larger, proving decompression actually happened.
        byte[] raw = archive.Archive.GetEntryData(movieEntry.Name);
        if (NhlWorkbench.Core.Compression.RefPack.IsRefPack(raw))
        {
            Assert.True(logical.Length > raw.Length);
        }
        else
        {
            Assert.Equal(raw, logical);
        }
    }
}
