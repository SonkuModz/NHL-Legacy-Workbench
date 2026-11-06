using System.Numerics;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// RX2 mesh files store neither normals nor tangents -- both are computed CPU-side from
/// geometry (and, for tangents, UVs), ported from the reference tool's
/// <c>_compute_smooth_normals</c>/<c>_compute_tangents</c>.
/// </summary>
public static class Rx2MeshGeometry
{
    /// <summary>Per-vertex smooth normals: unnormalized face normals accumulated at each triangle's three vertices, then normalized per vertex.</summary>
    public static Vector3[] ComputeSmoothNormals(Vector3[] positions, IReadOnlyList<int> triangleIndices)
    {
        var normals = new Vector3[positions.Length];

        for (int t = 0; t + 2 < triangleIndices.Count; t += 3)
        {
            int i0 = triangleIndices[t];
            int i1 = triangleIndices[t + 1];
            int i2 = triangleIndices[t + 2];

            Vector3 faceNormal = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);

            normals[i0] += faceNormal;
            normals[i1] += faceNormal;
            normals[i2] += faceNormal;
        }

        NormalizeInPlace(normals);
        return normals;
    }

    /// <summary>Per-vertex tangents: UV-determinant-weighted position deltas accumulated per triangle, then normalized per vertex.</summary>
    public static Vector3[] ComputeTangents(Vector3[] positions, Vector2[] uvs, IReadOnlyList<int> triangleIndices)
    {
        var tangents = new Vector3[positions.Length];

        for (int t = 0; t + 2 < triangleIndices.Count; t += 3)
        {
            int i0 = triangleIndices[t];
            int i1 = triangleIndices[t + 1];
            int i2 = triangleIndices[t + 2];

            Vector3 dv1 = positions[i1] - positions[i0];
            Vector3 dv2 = positions[i2] - positions[i0];

            Vector2 du1 = uvs[i1] - uvs[i0];
            Vector2 du2 = uvs[i2] - uvs[i0];

            float det = (du1.X * du2.Y) - (du2.X * du1.Y);
            if (MathF.Abs(det) < 1e-10f)
            {
                det = 1e-10f;
            }

            float f = 1f / det;
            Vector3 tangent = f * ((du2.Y * dv1) - (du1.Y * dv2));

            tangents[i0] += tangent;
            tangents[i1] += tangent;
            tangents[i2] += tangent;
        }

        NormalizeInPlace(tangents);
        return tangents;
    }

    private static void NormalizeInPlace(Vector3[] vectors)
    {
        for (int i = 0; i < vectors.Length; i++)
        {
            float length = vectors[i].Length();
            if (length > 0f)
            {
                vectors[i] /= length;
            }
        }
    }
}
