namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>A parsed RX2 mesh file -- may contain multiple submeshes (e.g. a player file's head/eyes/haircap).</summary>
public sealed class Rx2Model
{
    public required IReadOnlyList<Rx2Mesh> Meshes { get; init; }
}
