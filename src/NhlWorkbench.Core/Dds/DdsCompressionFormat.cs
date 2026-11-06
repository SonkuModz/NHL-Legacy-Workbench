namespace NhlWorkbench.Core.Dds;

/// <summary>
/// DDS Converter output formats. DXT2/DXT4 are premultiplied-alpha variants of DXT3/DXT5
/// with an identical block layout -- no current encoder (including DirectXTex) produces
/// true premultiplied-convention output, so they are encoded via the same BC2/BC3 path as
/// DXT3/DXT5 respectively. This is a decode-compatible, documented simplification, not a
/// silently dropped feature.
/// </summary>
public enum DdsCompressionFormat
{
    Dxt1,
    Dxt2,
    Dxt3,
    Dxt4,
    Dxt5,
    Bc7,
}
