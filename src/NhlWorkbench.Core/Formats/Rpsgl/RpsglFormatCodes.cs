using NhlWorkbench.Core.Containers;

namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>PlayStation 3 (RPSGL) texlib descriptor format code &lt;-&gt; <see cref="TextureFormat"/> mapping.</summary>
public static class RpsglFormatCodes
{
    private static readonly Dictionary<ushort, FormatElementInfo> ByCode = new()
    {
        [0x83F1] = new FormatElementInfo(TextureFormat.Dxt1, 8, PixelLayout.CompressedBlock),
        [0x83F2] = new FormatElementInfo(TextureFormat.Dxt3, 16, PixelLayout.CompressedBlock),
        [0x83F3] = new FormatElementInfo(TextureFormat.Dxt5, 16, PixelLayout.CompressedBlock),
        [0x6007] = new FormatElementInfo(TextureFormat.Argb8888, 4, PixelLayout.RawPixel),
    };

    private static readonly Dictionary<TextureFormat, ushort> ByFormat =
        ByCode.ToDictionary(kv => kv.Value.Format, kv => kv.Key);

    public static bool TryGetInfo(ushort formatCode, out FormatElementInfo info) => ByCode.TryGetValue(formatCode, out info);

    public static bool TryGetCode(TextureFormat format, out ushort formatCode) => ByFormat.TryGetValue(format, out formatCode);
}
