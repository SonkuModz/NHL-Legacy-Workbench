using NhlWorkbench.Core.Dds;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Containers;

/// <summary>
/// Wraps a standalone DDS blob (a bare .dds file, or a raw DDS entry found inside a BIGF
/// archive) as a one-texture <see cref="ITextureContainer"/> so it gets identical
/// preview/export/replace treatment to an RX2/RPSGL container through the same
/// format-agnostic seam, without the UI ever needing to special-case "just a DDS."
///
/// There is no wrapping header/descriptor table to preserve for a bare DDS -- the whole
/// point of "in place" elsewhere is to leave everything outside the pixel bytes
/// untouched, which is meaningless when the pixel bytes *are* the whole file. Rebuild
/// (re-wrap the current texture as a fresh DDS) is the only valid strategy.
/// </summary>
public sealed class SingleDdsContainer : ITextureContainer
{
    private readonly DdsCodec _codec;
    private TextureEntry _texture;

    public SingleDdsContainer(TextureEntry texture, string sourcePath, DdsCodec? codec = null)
    {
        _texture = texture;
        SourcePath = sourcePath;
        _codec = codec ?? new DdsCodec();
        Textures = new[] { _texture };
    }

    public ContainerFormat Format => ContainerFormat.Dds;

    public string SourcePath { get; }

    public IReadOnlyList<TextureEntry> Textures { get; private set; }

    public void ReplaceTexture(int textureIndex, TextureEntry replacement)
    {
        if (textureIndex != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureIndex), textureIndex, "A single-DDS container only ever has one texture, at index 0.");
        }

        _texture = replacement;
        Textures = new[] { _texture };
    }

    public PatchCapabilities GetPatchCapabilities() => PatchCapabilities.Rebuild;

    public byte[] Save(PatchStrategy strategy)
    {
        if (strategy != PatchStrategy.Rebuild)
        {
            throw new NotSupportedException($"SingleDdsContainer only supports {nameof(PatchStrategy.Rebuild)}.");
        }

        Result<byte[]> wrapped = _codec.WrapAsDds(_texture.LinearPixelData, _texture.Width, _texture.Height, _texture.Format, _texture.MipCount);
        if (!wrapped.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to re-wrap texture as DDS: {wrapped.Error}");
        }

        return wrapped.Value!;
    }
}
