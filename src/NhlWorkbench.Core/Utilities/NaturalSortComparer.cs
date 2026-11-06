namespace NhlWorkbench.Core.Utilities;

/// <summary>
/// Sorts strings so that embedded numbers compare by value rather than lexically
/// (e.g. "player_2" before "player_10"). Used for file lists so a folder scan reads
/// in the order a human expects.
/// </summary>
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix];
            char cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int startX = ix;
                int startY = iy;
                while (ix < x.Length && char.IsDigit(x[ix]))
                {
                    ix++;
                }

                while (iy < y.Length && char.IsDigit(y[iy]))
                {
                    iy++;
                }

                string numX = x[startX..ix].TrimStart('0');
                string numY = y[startY..iy].TrimStart('0');

                if (numX.Length != numY.Length)
                {
                    return numX.Length - numY.Length;
                }

                int numCompare = string.CompareOrdinal(numX, numY);
                if (numCompare != 0)
                {
                    return numCompare;
                }
            }
            else
            {
                if (cx != cy)
                {
                    return cx.CompareTo(cy);
                }

                ix++;
                iy++;
            }
        }

        return (x.Length - ix) - (y.Length - iy);
    }
}
