namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>One submesh's material: a name plus whichever texture-sampler slots the FxMaterial
/// entry actually references (diffuse/normal/specular) -- any of the texture names may be null
/// when that sampler slot isn't present on this material. Texture names refer to entries in the
/// same RX2 container's own texture list (<see cref="Rx2Reader"/>), matched by index.</summary>
public sealed record Rx2Material(
    string MaterialName,
    string? DiffuseTextureName,
    string? NormalTextureName,
    string? SpecularTextureName);
