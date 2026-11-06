using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Apt;
using NhlWorkbench.Core.Formats.Bigf;

namespace NhlWorkbench.Core.Formats.Overlay;

/// <summary>
/// Read-side analysis of one overlay .big archive: classifies every entry (<see
/// cref="OverlayEntryClassifier"/>) and parses the ones this workbench understands (the Apt
/// movie, the Apt constant file, shape records) into their typed models. Deliberately does not
/// wrap write access -- replacing a texture entry and saving is exactly the existing
/// <see cref="BigfArchive.GetEntryData"/>/<see cref="BigfArchive.RebuildReplacingEntries"/>
/// path the Texture Editor's BIG support already uses, so the Scorebug Editor's ViewModel
/// drives <see cref="Archive"/> directly for that instead of a second, parallel write API.
/// </summary>
public sealed class OverlayArchive
{
    private readonly Dictionary<string, byte[]> _decompressedByName = new(StringComparer.Ordinal);

    private OverlayArchive(BigfArchive archive, IReadOnlyList<OverlayArchiveEntry> entries)
    {
        Archive = archive;
        Entries = entries;
    }

    /// <summary>The underlying archive -- use this directly for <c>GetEntryData</c>/<c>RebuildReplacingEntries</c>.</summary>
    public BigfArchive Archive { get; }

    public IReadOnlyList<OverlayArchiveEntry> Entries { get; }

    public static Result<OverlayArchive> Open(byte[] data, string sourcePath)
    {
        Result<BigfArchive> parsed = BigfArchive.Parse(data, sourcePath);
        if (!parsed.IsSuccess)
        {
            return Result.Failure<OverlayArchive>(parsed.Error!, parsed.Exception);
        }

        BigfArchive archive = parsed.Value!;
        var overlay = new OverlayArchive(archive, BuildEntries(archive, out Dictionary<string, byte[]> decompressed));
        foreach ((string name, byte[] bytes) in decompressed)
        {
            overlay._decompressedByName[name] = bytes;
        }

        return Result.Success(overlay);
    }

    private static List<OverlayArchiveEntry> BuildEntries(BigfArchive archive, out Dictionary<string, byte[]> decompressed)
    {
        decompressed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var results = new List<OverlayArchiveEntry>(archive.Entries.Count);
        foreach (BigfEntry entry in archive.Entries)
        {
            byte[] entryData;
            try
            {
                entryData = archive.GetEntryData(entry.Name);
            }
            catch
            {
                results.Add(new OverlayArchiveEntry(entry.Name, entry.Offset, entry.Size, OverlayEntryKind.Unknown));
                continue;
            }

            OverlayEntryKind kind = OverlayEntryClassifier.Classify(entryData, out byte[]? inner);
            if (inner is not null)
            {
                decompressed[entry.Name] = inner;
            }

            results.Add(new OverlayArchiveEntry(entry.Name, entry.Offset, entry.Size, kind));
        }

        return results;
    }

    /// <summary>Raw bytes for a named entry, transparently RefPack-decompressed when that entry turned out to be a compressed Movie/ConstantFile (classification already paid the decompression cost once).</summary>
    public byte[] GetLogicalEntryData(string name)
    {
        if (_decompressedByName.TryGetValue(name, out byte[]? cached))
        {
            return cached;
        }

        byte[] raw = Archive.GetEntryData(name);
        return RefPack.IsRefPack(raw) && RefPack.Decompress(raw) is { IsSuccess: true } dec ? dec.Value! : raw;
    }

    public Result<AptMovie> GetMovie()
    {
        OverlayArchiveEntry? entry = Entries.FirstOrDefault(e => e.Kind == OverlayEntryKind.Movie);
        return entry is null
            ? Result.Failure<AptMovie>("No Apt movie entry found in this archive.")
            : AptMovieReader.Parse(GetLogicalEntryData(entry.Name));
    }

    public Result<AptConstantFile> GetConstantFile()
    {
        OverlayArchiveEntry? entry = Entries.FirstOrDefault(e => e.Kind == OverlayEntryKind.ConstantFile);
        return entry is null
            ? Result.Failure<AptConstantFile>("No Apt constant file entry found in this archive.")
            : AptConstantFileReader.Parse(GetLogicalEntryData(entry.Name));
    }

    public IReadOnlyList<(OverlayArchiveEntry Entry, AptShapeRecord Shape)> GetShapeRecords()
    {
        var results = new List<(OverlayArchiveEntry, AptShapeRecord)>();
        foreach (OverlayArchiveEntry entry in Entries.Where(e => e.Kind == OverlayEntryKind.ShapeRecord))
        {
            Result<AptShapeRecord> parsed = AptShapeRecordReader.Parse(GetLogicalEntryData(entry.Name));
            if (parsed.IsSuccess)
            {
                results.Add((entry, parsed.Value!));
            }
        }

        return results;
    }

    public IReadOnlyList<OverlayArchiveEntry> GetTextureEntries() =>
        Entries.Where(e => e.Kind == OverlayEntryKind.Texture).ToList();

    /// <summary>
    /// Resolves an <see cref="AptShapeCharacter.GeometryKey"/> to the archive entry that backs
    /// it. Confirmed 2026-08-19 across every Shape in 3 real overlay archives (33/33 matches,
    /// 0 misses): the entry's name is the literal decimal string of <paramref name="geometryKey"/>
    /// + 2. The resolved entry is usually a <see cref="OverlayEntryKind.ShapeRecord"/> (real
    /// vector geometry), but not always -- a handful of real keys (including the documented
    /// geometryKey==0 "plain rectangle fill" sentinel from the external community research this
    /// binding was cross-checked against, References/aptresearch/NCAA14_APT_SPEC.md) resolve
    /// to a <see cref="OverlayEntryKind.Texture"/> entry instead, meaning that Shape is really
    /// just an image-filled rect with no separate vector geometry. Both are valid, real
    /// resolutions; this method returns whichever kind the archive actually has at that name,
    /// and null only when no entry with that name exists at all.
    /// </summary>
    public OverlayArchiveEntry? TryResolveShapeGeometryEntry(int geometryKey)
    {
        string entryName = (geometryKey + 2).ToString();
        return Entries.FirstOrDefault(e => e.Name == entryName);
    }
}
