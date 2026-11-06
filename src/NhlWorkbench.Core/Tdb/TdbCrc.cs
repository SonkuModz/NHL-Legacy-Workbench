namespace NhlWorkbench.Core.Tdb;

/// <summary>
/// The CRC every inner-DB checksum slot uses: CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF,
/// no reflection, no final XOR). Same routine Heck's editor calls <c>calc_crc</c> and the NHL
/// Modding Studio spec calls "Routine A" (known answer for "123456789" is 0x0376E6E7).
/// </summary>
public static class TdbCrc
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ Table[(crc >> 24) ^ b];
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            table[i] = c;
        }

        return table;
    }
}
