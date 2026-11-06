using System.Numerics;

namespace NhlWorkbench.Core.ModelConversion;

/// <summary>One submesh's worth of geometry read back from an external format (OBJ or glTF), ready for <see cref="NhlWorkbench.Core.Formats.Rx2.Rx2ModelWriter"/>.</summary>
public sealed record ImportedSubmesh(string Name, Vector3[] Positions, Vector2[] Uvs, int[] TriangleIndices);
