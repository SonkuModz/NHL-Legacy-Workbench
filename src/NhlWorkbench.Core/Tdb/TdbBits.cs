namespace NhlWorkbench.Core.Tdb;

/// <summary>Big-endian, MSB-first bit packing -- how every PS3/Xbox 360/Legacy TDB record packs its fields.</summary>
public static class TdbBits
{
    public static ulong Read(ReadOnlySpan<byte> data, long bitOffset, int width)
    {
        ulong value = 0;
        for (int i = 0; i < width; i++)
        {
            long bit = bitOffset + i;
            int b = (data[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1;
            value = (value << 1) | (uint)b;
        }

        return value;
    }

    public static void Write(Span<byte> data, long bitOffset, int width, ulong value)
    {
        for (int i = 0; i < width; i++)
        {
            long bit = bitOffset + i;
            int idx = (int)(bit >> 3);
            int shift = 7 - (int)(bit & 7);
            bool set = ((value >> (width - 1 - i)) & 1UL) != 0;
            data[idx] = set ? (byte)(data[idx] | (1 << shift)) : (byte)(data[idx] & ~(1 << shift));
        }
    }
}
