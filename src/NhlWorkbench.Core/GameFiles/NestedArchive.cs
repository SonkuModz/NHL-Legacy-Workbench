using NhlWorkbench.Core.Compression;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Bigf;
using NhlWorkbench.Core.Formats.Eb;

namespace NhlWorkbench.Core.GameFiles;

public sealed record NestedEntry(string Name, long StoredSize);

/// <summary>
/// An archive held in memory, usually a member of a larger archive: BIGF, BIG4 or EB. Member payloads
/// are unwrapped when read, so callers see RefPack and chunkzip members as plain bytes.
/// </summary>
public sealed class NestedArchive
{
    private readonly BigfArchive? _bigf;
    private readonly EbArchive? _eb;

    private NestedArchive(string sourceName, BigfArchive? bigf, EbArchive? eb, IReadOnlyList<NestedEntry> entries)
    {
        SourceName = sourceName;
        _bigf = bigf;
        _eb = eb;
        Entries = entries;
    }

    public string SourceName { get; }

    public IReadOnlyList<NestedEntry> Entries { get; }

    public static bool IsArchive(byte[] data) => BigfArchive.IsBigfOrBig4(data) || EbArchive.IsEbArchive(data);

    public static Result<NestedArchive> Open(byte[] data, string sourceName)
    {
        if (BigfArchive.IsBigfOrBig4(data))
        {
            Result<BigfArchive> bigf = BigfArchive.Parse(data, sourceName);
            if (!bigf.IsSuccess)
            {
                return Result.Failure<NestedArchive>(bigf.Error!, bigf.Exception);
            }

            NestedEntry[] entries = bigf.Value!.Entries.Select(e => new NestedEntry(e.Name, e.Size)).ToArray();
            return Result.Success(new NestedArchive(sourceName, bigf.Value, null, entries), bigf.Warnings);
        }

        if (EbArchive.IsEbArchive(data))
        {
            Result<EbArchive> eb = EbArchive.Open(new MemoryStream(data, writable: false), sourceName);
            if (!eb.IsSuccess)
            {
                return Result.Failure<NestedArchive>(eb.Error!, eb.Exception);
            }

            NestedEntry[] entries = eb.Value!.Entries.Select(e => new NestedEntry(e.Path, e.StoredSize)).ToArray();
            return Result.Success(new NestedArchive(sourceName, null, eb.Value, entries), eb.Warnings);
        }

        return Result.Failure<NestedArchive>($"{sourceName} isn't an archive.");
    }

    /// <summary>Reads a member by its exact name and removes any RefPack or chunkzip wrapper.</summary>
    public Result<byte[]> Read(string name)
    {
        if (_eb is not null)
        {
            EbEntry? entry = _eb.Find(name);
            return entry is null ? Missing(name) : _eb.ReadDecoded(entry);
        }

        if (_bigf!.Entries.All(e => e.Name != name))
        {
            return Missing(name);
        }

        byte[] raw;
        try
        {
            raw = _bigf.GetEntryData(name);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<byte[]>($"{SourceName}!{name} lies outside the archive.", ex);
        }

        if (ChunkZip.IsChunkZip(raw))
        {
            return ChunkZip.Decompress(raw);
        }

        // A plain file can resemble a RefPack header, so a failed decode means it wasn't one.
        return RefPack.IsRefPack(raw) && RefPack.Decompress(raw) is { IsSuccess: true } unpacked
            ? unpacked
            : Result.Success(raw);
    }

    private Result<byte[]> Missing(string name) =>
        Result.Failure<byte[]>($"{SourceName} has no member named {name}.");
}
