using System.Text;

namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>Small shared helper for the two APT string-table shapes seen in overlay archives: a run of printable ASCII bounded by non-printable bytes on both ends (used for the movie/constant-file version tag) and a flat scan for every such run in a buffer (used to build <see cref="AptMovie.AllStrings"/> and <see cref="AptConstantFile.AllStrings"/>).</summary>
internal static class AptStringScanner
{
    /// <summary>Reads consecutive printable-ASCII bytes (0x20-0x7E) starting at <paramref name="offset"/>, stopping at the first byte outside that range or the end of the buffer.</summary>
    public static string ReadPrintableRun(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] is >= 0x20 and <= 0x7E)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    /// <summary>Finds every run of printable ASCII at least <paramref name="minLength"/> bytes long anywhere in <paramref name="data"/>, with its starting offset.</summary>
    public static List<(int Offset, string Value)> ScanAll(byte[] data, int minLength)
    {
        var results = new List<(int Offset, string Value)>();
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] is >= 0x20 and <= 0x7E)
            {
                int start = i;
                while (i < data.Length && data[i] is >= 0x20 and <= 0x7E)
                {
                    i++;
                }

                if (i - start >= minLength)
                {
                    results.Add((start, Encoding.ASCII.GetString(data, start, i - start)));
                }
            }
            else
            {
                i++;
            }
        }

        return results;
    }
}
