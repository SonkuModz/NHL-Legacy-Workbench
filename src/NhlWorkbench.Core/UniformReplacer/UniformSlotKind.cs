namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>The 3 texture roles shared by every uniform category covered so far. Socks and goalie
/// helmets only ever populate <see cref="ColorMap"/> (confirmed by decoding real containers: each
/// has exactly one texture) -- <see cref="NormalMap"/>/<see cref="SpecularMap"/> simply stay absent
/// for those categories rather than needing a separate enum shape per category.</summary>
public enum UniformSlotKind
{
    ColorMap,
    NormalMap,
    SpecularMap,
}
