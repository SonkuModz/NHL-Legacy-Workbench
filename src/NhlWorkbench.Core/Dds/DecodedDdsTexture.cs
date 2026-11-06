using NhlWorkbench.Core.Containers;

namespace NhlWorkbench.Core.Dds;

/// <summary>A texture read from a standalone .dds file -- <see cref="LinearData"/> is the full mip chain, concatenated, with no DDS header, directly usable as a <c>TextureEntry.LinearPixelData</c> replacement.</summary>
public sealed record DecodedDdsTexture(TextureFormat Format, int Width, int Height, int MipCount, byte[] LinearData);
