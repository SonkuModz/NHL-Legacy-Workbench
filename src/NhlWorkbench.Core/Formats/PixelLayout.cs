using NhlWorkbench.Core.Containers;

namespace NhlWorkbench.Core.Formats;

/// <summary>Whether a format's data is 4x4-pixel compressed blocks or individual raw pixels -- shared by both RX2 and RPSGL format tables since it affects mip-chain size math identically on both platforms.</summary>
public enum PixelLayout
{
    CompressedBlock,
    RawPixel,
}

public readonly record struct FormatElementInfo(TextureFormat Format, int BytesPerElement, PixelLayout Layout);
