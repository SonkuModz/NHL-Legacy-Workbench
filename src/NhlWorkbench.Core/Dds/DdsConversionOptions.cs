namespace NhlWorkbench.Core.Dds;

public sealed class DdsConversionOptions
{
    public bool GenerateMipmaps { get; init; }

    /// <summary>When false, alpha is forced fully opaque before compression rather than carried through as-is.</summary>
    public bool PreserveAlpha { get; init; } = true;

    public (int Width, int Height)? ResizeTo { get; init; }

    public static DdsConversionOptions Default { get; } = new();
}
