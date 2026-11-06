namespace NhlWorkbench.Core.Dds;

/// <summary>A decoded, uncompressed RGBA8 image -- tightly packed, row-major, no padding. WPF-free so Core has no UI dependency.</summary>
public sealed class Rgba32Image
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>width * height * 4 bytes, R8G8B8A8 per pixel.</summary>
    public required byte[] Pixels { get; init; }
}
