using System.Buffers.Binary;
using System.Text;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Formats.Apt;
using Xunit;

namespace NhlWorkbench.Core.Tests;

/// <summary>Synthetic-input coverage for <see cref="AptMovieReader"/>'s edge cases, independent of the real-file corpus (see <c>OverlayArchiveRealFileTests</c> for ground-truth coverage against actual game data).</summary>
public class AptMovieReaderTests
{
    /// <summary>Builds a minimal, well-formed synthetic movie: version string, tag byte, padding, then <paramref name="importNames"/>.Count + 1 class-reference groups (self + imports), with a string pool immediately following.</summary>
    private static byte[] BuildSyntheticMovie(string classPath, string className, params (string Path, string Name)[] imports)
    {
        var strings = new List<string> { classPath, className };
        foreach ((string path, string name) in imports)
        {
            strings.Add(path);
            strings.Add(name);
        }

        int groupCount = imports.Length + 1;
        const string version = "Apt Data:1:7:4";
        int tagByteOffset = version.Length;
        int tableStart = ((tagByteOffset + 1) + 3) / 4 * 4;
        int poolStart = tableStart + (groupCount * 16);

        var poolOffsets = new List<int>();
        var pool = new List<byte>();
        foreach (string s in strings)
        {
            poolOffsets.Add(poolStart + pool.Count);
            pool.AddRange(Encoding.ASCII.GetBytes(s));
            pool.Add(0);
        }

        var buffer = new byte[poolStart + pool.Count];
        Encoding.ASCII.GetBytes(version).CopyTo(buffer, 0);
        buffer[tagByteOffset] = 0x1A;

        for (int g = 0; g < groupCount; g++)
        {
            int groupOffset = tableStart + (g * 16);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(groupOffset, 4), (uint)poolOffsets[g * 2]);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(groupOffset + 4, 4), (uint)poolOffsets[(g * 2) + 1]);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(groupOffset + 8, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(groupOffset + 12, 4), 0);
        }

        pool.CopyTo(buffer, poolStart);
        return buffer;
    }

    [Fact]
    public void Parse_NoImports_ResolvesOnlySelfClass()
    {
        byte[] movie = BuildSyntheticMovie("game/overlays/x", "X");
        Result<AptMovie> result = AptMovieReader.Parse(movie);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("X", result.Value!.ClassName);
        Assert.Equal("game/overlays/x", result.Value!.ClassPath);
        Assert.Empty(result.Value!.Imports);
    }

    [Fact]
    public void Parse_ManyImports_DerivesGroupCountFromFirstOffsetAlone()
    {
        byte[] movie = BuildSyntheticMovie(
            "game/overlays/y", "Y",
            ("external/a", "A"),
            ("external/b", "B"),
            ("external/c", "C"),
            ("external/d", "D"));

        Result<AptMovie> result = AptMovieReader.Parse(movie);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(4, result.Value!.Imports.Count());
        Assert.Equal(new[] { "A", "B", "C", "D" }, result.Value!.Imports.Select(i => i.ShortName));
    }

    [Fact]
    public void Parse_MissingVersionPrefix_Fails()
    {
        byte[] junk = Encoding.ASCII.GetBytes("Not An Apt Movie At All");
        Result<AptMovie> result = AptMovieReader.Parse(junk);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Parse_TruncatedAfterVersionString_FailsWithoutThrowing()
    {
        byte[] truncated = Encoding.ASCII.GetBytes("Apt Data:1:7:4");
        Result<AptMovie> result = AptMovieReader.Parse(truncated);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Parse_CorruptGroupCountArithmetic_FailsRatherThanReadingGarbage()
    {
        byte[] movie = BuildSyntheticMovie("game/overlays/z", "Z");

        // Corrupt group 1's first field so (value - tableStart) is not a multiple of 16.
        int tableStart = 16;
        BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(tableStart, 4), (uint)(tableStart + 5));

        Result<AptMovie> result = AptMovieReader.Parse(movie);
        Assert.False(result.IsSuccess);
    }
}
