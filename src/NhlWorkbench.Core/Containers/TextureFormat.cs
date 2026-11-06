namespace NhlWorkbench.Core.Containers;

/// <summary>
/// Every texture format code found in RX2/RPSGL containers, plus BC7 which only ever
/// appears as a DDS Converter output (neither console container format produces it).
/// Each value maps 1:1 to a specific raw format byte in a specific container format, so
/// round-tripping never loses information (e.g. <see cref="Dxt1NormalPacked"/> and
/// <see cref="Dxt1"/> decode identically as BC1 but are written back with different
/// raw codes).
/// </summary>
public enum TextureFormat
{
    /// <summary>BC1. PS3 code 0x83F1, X360 code 0x52.</summary>
    Dxt1,

    /// <summary>BC1 block layout, used as a packed normal map by convention. X360 code 0x7C only.</summary>
    Dxt1NormalPacked,

    /// <summary>BC2 with premultiplied alpha convention (same block layout as <see cref="Dxt3"/>). DDS Converter format list only.</summary>
    Dxt2,

    /// <summary>BC2. PS3 code 0x83F2, X360 code 0x53.</summary>
    Dxt3,

    /// <summary>BC3 with premultiplied alpha convention (same block layout as <see cref="Dxt5"/>). DDS Converter format list only.</summary>
    Dxt4,

    /// <summary>BC3. PS3 code 0x83F3, X360 code 0x54.</summary>
    Dxt5,

    /// <summary>BC7. DDS Converter output only; not produced by either console container format.</summary>
    Bc7,

    /// <summary>BC5 (ATI2/DXN), used as a packed normal map. X360 code 0x71 only.</summary>
    Bc5NormalMap,

    /// <summary>Raw 32bpp. PS3 code 0x6007, X360 code 0x86.</summary>
    Argb8888,

    /// <summary>Raw 16bpp, 1-5-5-5. X360 code 0x43 only.</summary>
    A1R5G5B5,

    /// <summary>Raw 16bpp, 5-6-5. X360 code 0x44 only.</summary>
    B5G6R5,
}
