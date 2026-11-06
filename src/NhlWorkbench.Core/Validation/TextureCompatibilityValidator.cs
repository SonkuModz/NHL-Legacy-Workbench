using NhlWorkbench.Core.Containers;
using NhlWorkbench.Core.Dds;

namespace NhlWorkbench.Core.Validation;

/// <summary>
/// The Width/Height/Format/MipCount compatibility check used whenever a texture replacement is
/// staged into an existing container slot. Shared so every consumer (Jersey Importer, and
/// eventually the Texture Editor / Arena Texture Editor / Rink Editor, which each duplicated this
/// check inline before this type existed) reports mismatches identically.
/// </summary>
public static class TextureCompatibilityValidator
{
    public static bool AreCompatible(TextureEntry original, DecodedDdsTexture replacement)
        => original.Width == replacement.Width
            && original.Height == replacement.Height
            && original.Format == replacement.Format
            && original.MipCount == replacement.MipCount;

    /// <summary>A human-readable diff of every field that differs, or null if they're fully compatible.</summary>
    public static string? DescribeMismatch(TextureEntry original, DecodedDdsTexture replacement)
    {
        if (AreCompatible(original, replacement))
        {
            return null;
        }

        var parts = new List<string>();

        if (original.Width != replacement.Width || original.Height != replacement.Height)
        {
            parts.Add($"dimensions {replacement.Width}x{replacement.Height} vs original {original.Width}x{original.Height}");
        }

        if (original.Format != replacement.Format)
        {
            parts.Add($"format {replacement.Format} vs original {original.Format}");
        }

        if (original.MipCount != replacement.MipCount)
        {
            parts.Add($"mip count {replacement.MipCount} vs original {original.MipCount}");
        }

        return string.Join("; ", parts);
    }
}
