namespace NhlWorkbench.Core.Containers;

public enum ContainerFormat
{
    /// <summary>Xbox 360 texture container (magic <c>\x89RW4xb2</c>).</summary>
    Rx2,

    /// <summary>PlayStation 3 texture container (magic <c>\x89RW4ps3</c>).</summary>
    Rpsgl,

    /// <summary>An RX2 or RPSGL container wrapped in an outer BIGF archive.</summary>
    BigfWrapped,

    /// <summary>A standalone .dds file (or a raw DDS blob found inside a BIGF entry), wrapped as a one-texture container.</summary>
    Dds,

    /// <summary>Xbox 360 XPR2 packed resource (usually RefPack-compressed inside a BIGF entry) holding one TX2D texture.</summary>
    Xpr,
}
