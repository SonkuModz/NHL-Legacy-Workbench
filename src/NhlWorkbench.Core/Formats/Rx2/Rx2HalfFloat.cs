namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Big-endian IEEE 754 half-float decode. RX2 mesh vertex positions are stored as three
/// consecutive big-endian half-floats (6 bytes); this is the only place Core needs
/// float16 decoding (texture parsing never does).
/// </summary>
public static class Rx2HalfFloat
{
    public static float Decode(byte highByte, byte lowByte)
    {
        int bits = (highByte << 8) | lowByte;
        int sign = (bits >> 15) & 1;
        int exponent = (bits >> 10) & 0x1F;
        int mantissa = bits & 0x3FF;

        float value;
        if (exponent == 0)
        {
            value = (mantissa / 1024f) * MathF.Pow(2f, -14f);
        }
        else if (exponent == 31)
        {
            value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
        }
        else
        {
            value = (1f + (mantissa / 1024f)) * MathF.Pow(2f, exponent - 15);
        }

        return sign == 0 ? value : -value;
    }

    /// <summary>Decodes three consecutive big-endian half-floats starting at <paramref name="offset"/>.</summary>
    public static System.Numerics.Vector3 DecodeVector3(byte[] data, int offset) =>
        new(
            Decode(data[offset], data[offset + 1]),
            Decode(data[offset + 2], data[offset + 3]),
            Decode(data[offset + 4], data[offset + 5]));

    /// <summary>Round-to-nearest-even IEEE 754 half-float encode, inverse of <see cref="Decode"/>. Values outside half-float range saturate to +-infinity rather than throwing -- mesh coordinates that overflow this are a modeling-tool problem, not something this encoder should crash on.</summary>
    public static (byte HighByte, byte LowByte) Encode(float value)
    {
        uint bits32 = BitConverter.SingleToUInt32Bits(value);
        uint sign = (bits32 >> 16) & 0x8000;
        int exponent32 = (int)((bits32 >> 23) & 0xFF) - 127;
        uint mantissa32 = bits32 & 0x7FFFFF;

        ushort result;
        if (float.IsNaN(value))
        {
            result = (ushort)(sign | 0x7E00);
        }
        else if (exponent32 > 15)
        {
            result = (ushort)(sign | 0x7C00); // overflow -> infinity
        }
        else if (exponent32 < -24)
        {
            result = (ushort)sign; // underflow -> zero
        }
        else if (exponent32 < -14)
        {
            // Subnormal half-float: shift the implicit-1 mantissa right by the extra exponent deficit.
            int shift = -14 - exponent32;
            uint mantissaWithImplicitBit = mantissa32 | 0x800000;
            result = (ushort)(sign | (mantissaWithImplicitBit >> (shift + 13)));
        }
        else
        {
            ushort halfExponent = (ushort)((exponent32 + 15) << 10);
            ushort halfMantissa = (ushort)(mantissa32 >> 13);
            result = (ushort)(sign | halfExponent | halfMantissa);
        }

        return ((byte)(result >> 8), (byte)result);
    }

    /// <summary>Encodes three components as consecutive big-endian half-floats starting at <paramref name="offset"/>.</summary>
    public static void EncodeVector3(byte[] destination, int offset, System.Numerics.Vector3 value)
    {
        (destination[offset], destination[offset + 1]) = Encode(value.X);
        (destination[offset + 2], destination[offset + 3]) = Encode(value.Y);
        (destination[offset + 4], destination[offset + 5]) = Encode(value.Z);
    }
}
