using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Formats.Rx2;

namespace NhlWorkbench.Core.StadiumEditor;

/// <summary>One stadium submesh plus its resolved material and already-decoded textures (decoded
/// once at load time, not per-frame -- a stadium container can have well over a hundred submeshes).</summary>
public sealed class StadiumSubmesh
{
    public required int Index { get; init; }

    public required Rx2Mesh Mesh { get; init; }

    /// <summary>Null when this submesh has no resolvable material data -- normal for many submeshes, not an error.</summary>
    public Rx2Material? Material { get; init; }

    public Rgba32Image? DiffuseMap { get; init; }

    public Rgba32Image? NormalMap { get; init; }

    public Rgba32Image? SpecularMap { get; init; }

    /// <summary>The original (still natively-compressed) texture entries backing the three maps
    /// above -- kept alongside the decoded RGBA versions so Export DDS can write out the exact
    /// original bytes (no lossy re-encode) and Replace DDS knows which texture index to patch.</summary>
    public TextureEntry? DiffuseTexture { get; init; }

    public TextureEntry? NormalTexture { get; init; }

    public TextureEntry? SpecularTexture { get; init; }
}
