namespace NhlWorkbench.Core.Containers;

/// <summary>
/// The format-neutral texture DTO the UI binds to regardless of whether it came from
/// an RX2 or RPSGL container. Format-specific write-back bookkeeping (GPU offset/size,
/// descriptor offset, etc.) is kept out of the public surface via
/// <see cref="WriteBackMetadata"/> so callers never need a format-specific cast just to
/// read a texture's name/dimensions/pixels.
/// </summary>
public sealed class TextureEntry
{
    public required string Name { get; set; }

    /// <summary>Position of this texture within its container's texture list; needed to address write-back correctly.</summary>
    public int Index { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public TextureFormat Format { get; set; }

    public int MipCount { get; set; }

    /// <summary>Already de-tiled (X360) / already extracted (PS3) block or raw pixel bytes for the full mip chain — this is the format-neutral boundary the DDS codec operates on.</summary>
    public byte[] LinearPixelData { get; set; } = Array.Empty<byte>();

    /// <summary>Opaque, format-specific data populated by that format's reader and consumed only by that same format's writer.</summary>
    internal object? WriteBackMetadata { get; set; }
}
