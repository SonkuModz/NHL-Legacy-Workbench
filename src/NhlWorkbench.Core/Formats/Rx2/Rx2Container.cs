using NhlWorkbench.Core.Containers;

namespace NhlWorkbench.Core.Formats.Rx2;

public sealed class Rx2Container : ITextureContainer
{
    private readonly byte[] _originalBytes;
    private readonly Dictionary<int, TextureEntry> _staged = new();

    internal Rx2Container(byte[] originalBytes, string sourcePath, List<TextureEntry> textures)
    {
        _originalBytes = originalBytes;
        SourcePath = sourcePath;
        Textures = textures;
    }

    public ContainerFormat Format => ContainerFormat.Rx2;

    public string SourcePath { get; }

    public IReadOnlyList<TextureEntry> Textures { get; }

    public void ReplaceTexture(int textureIndex, TextureEntry replacement)
    {
        if (textureIndex < 0 || textureIndex >= Textures.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(textureIndex));
        }

        _staged[textureIndex] = replacement;
    }

    public PatchCapabilities GetPatchCapabilities()
    {
        if (_staged.Count == 0)
        {
            return PatchCapabilities.InPlace | PatchCapabilities.Rebuild;
        }

        bool allMatchOriginalShape = _staged.All(kv =>
        {
            TextureEntry original = Textures[kv.Key];
            TextureEntry replacement = kv.Value;
            return replacement.Width == original.Width
                && replacement.Height == original.Height
                && replacement.Format == original.Format
                && replacement.MipCount == original.MipCount;
        });

        return allMatchOriginalShape ? PatchCapabilities.InPlace | PatchCapabilities.Rebuild : PatchCapabilities.Rebuild;
    }

    public byte[] Save(PatchStrategy strategy)
    {
        if (strategy == PatchStrategy.InPlace)
        {
            if (!GetPatchCapabilities().HasFlag(PatchCapabilities.InPlace))
            {
                throw new InvalidOperationException(
                    "InPlace patch is not valid for the currently staged replacements: at least one differs from its original in format, dimensions, or mip count.");
            }

            return Rx2Writer.PatchInPlace(_originalBytes, Textures, _staged);
        }

        return Rx2Writer.Rebuild(_originalBytes, Textures, _staged);
    }
}
