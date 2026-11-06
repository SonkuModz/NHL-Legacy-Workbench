namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>Converts an RX2 index buffer (triangle list or triangle strip) to a flat triangle-index list.</summary>
public static class Rx2TriangleStrip
{
    private const ushort RestartIndex = 0xFFFF;

    /// <summary>Primitive type 4: indices are already grouped in triplets.</summary>
    public static List<int> FromTriangleList(ushort[] indices)
    {
        var triangles = new List<int>(indices.Length);
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            triangles.Add(indices[i]);
            triangles.Add(indices[i + 1]);
            triangles.Add(indices[i + 2]);
        }

        return triangles;
    }

    /// <summary>
    /// Primitive type 6: a sequence of triangle strips separated by the 0xFFFF restart
    /// code. Within each strip, winding alternates every triangle (standard strip
    /// convention) so face normals stay consistent; degenerate triangles (any two
    /// indices equal, used to bridge strips without a restart code) are skipped.
    /// </summary>
    public static List<int> FromTriangleStrip(ushort[] indices)
    {
        var triangles = new List<int>();
        var strip = new List<ushort>();

        void FlushStrip()
        {
            for (int k = 0; k + 2 < strip.Count; k++)
            {
                ushort a = strip[k];
                ushort b = strip[k + 1];
                ushort c = strip[k + 2];

                if (a == b || b == c || a == c)
                {
                    continue;
                }

                if (k % 2 == 0)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
                else
                {
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                }
            }

            strip.Clear();
        }

        foreach (ushort index in indices)
        {
            if (index == RestartIndex)
            {
                FlushStrip();
            }
            else
            {
                strip.Add(index);
            }
        }

        FlushStrip();

        return triangles;
    }
}
