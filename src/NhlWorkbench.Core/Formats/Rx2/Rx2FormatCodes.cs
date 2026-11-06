using NhlWorkbench.Core.Containers;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>Xbox 360 (RX2) texture descriptor format byte &lt;-&gt; <see cref="TextureFormat"/> mapping.</summary>
public static class Rx2FormatCodes
{
    private static readonly Dictionary<byte, FormatElementInfo> ByCode = new()
    {
        [0x52] = new FormatElementInfo(TextureFormat.Dxt1, 8, PixelLayout.CompressedBlock),
        [0x53] = new FormatElementInfo(TextureFormat.Dxt3, 16, PixelLayout.CompressedBlock),
        [0x54] = new FormatElementInfo(TextureFormat.Dxt5, 16, PixelLayout.CompressedBlock),
        [0x71] = new FormatElementInfo(TextureFormat.Bc5NormalMap, 16, PixelLayout.CompressedBlock),
        [0x7C] = new FormatElementInfo(TextureFormat.Dxt1NormalPacked, 8, PixelLayout.CompressedBlock),
        [0x86] = new FormatElementInfo(TextureFormat.Argb8888, 4, PixelLayout.RawPixel),
        [0x43] = new FormatElementInfo(TextureFormat.A1R5G5B5, 2, PixelLayout.RawPixel),
        [0x44] = new FormatElementInfo(TextureFormat.B5G6R5, 2, PixelLayout.RawPixel),
    };

    private static readonly Dictionary<TextureFormat, byte> ByFormat =
        ByCode.ToDictionary(kv => kv.Value.Format, kv => kv.Key);

    public static bool TryGetInfo(byte formatCode, out FormatElementInfo info) => ByCode.TryGetValue(formatCode, out info);

    public static bool TryGetCode(TextureFormat format, out byte formatCode) => ByFormat.TryGetValue(format, out formatCode);
}
