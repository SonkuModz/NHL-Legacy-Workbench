using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Containers;

/// <summary>
/// The format-agnostic seam the UI binds to. <see cref="Formats.Rx2.Rx2Container"/> and
/// <see cref="Formats.Rpsgl.RpsglContainer"/> are the two implementations; callers never
/// branch on <see cref="Format"/> except to display it.
/// </summary>
public interface ITextureContainer
{
    ContainerFormat Format { get; }

    string SourcePath { get; }

    IReadOnlyList<TextureEntry> Textures { get; }

    /// <summary>
    /// Stages <paramref name="replacement"/> as the pending contents of the texture at
    /// <paramref name="textureIndex"/>. Does not touch the underlying file until
    /// <see cref="Save"/> is called.
    /// </summary>
    void ReplaceTexture(int textureIndex, TextureEntry replacement);

    /// <summary>
    /// Which write-back strategies are currently valid given every staged replacement
    /// (see <see cref="ReplaceTexture"/>). Rebuild is always possible; InPlace requires
    /// every staged replacement to match its original's format/dimensions/mip count.
    /// </summary>
    PatchCapabilities GetPatchCapabilities();

    /// <summary>
    /// Produces the new container bytes reflecting all staged replacements. Never
    /// throws for malformed *input* (that was already handled at parse time) — it can
    /// throw only for programmer error, i.e. requesting a strategy
    /// <see cref="GetPatchCapabilities"/> reported as unsupported.
    /// </summary>
    byte[] Save(PatchStrategy strategy);
}
