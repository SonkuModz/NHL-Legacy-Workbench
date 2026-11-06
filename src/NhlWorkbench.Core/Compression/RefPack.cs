using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Compression;

/// <summary>
/// EA RefPack (QFS-family) compression, the codec behind the <c>0x??FB</c> two-byte
/// signature that wraps texture payloads inside this game's front-end BIGF archives
/// (team logos etc.). The decoder was validated byte-for-byte against QuickBMS's
/// reference <c>COMP_DK2</c> implementation on a real game entry
/// (d24.big entry "96" -- see <c>XprContainerTests</c>), not written from a description.
///
/// The encoder deliberately emits only literal runs (no back-reference matching):
/// always-correct output any conforming decoder accepts, at the cost of ~0.9% size
/// overhead versus the raw payload. Real compression matters little here -- the entries
/// this wraps are small UI textures -- and a trivial encoder can't have match-finder bugs.
/// </summary>
public static class RefPack
{
    /// <summary>The two-byte signature: flags byte (low bits per the QFS convention) then 0xFB.</summary>
    public static bool IsRefPack(byte[] data) =>
        data.Length >= 5 && data[1] == 0xFB && (data[0] & 0x3E) == 0x10;

    public static Result<byte[]> Decompress(byte[] input)
    {
        try
        {
            if (!IsRefPack(input))
            {
                return Result.Failure<byte[]>("Not a RefPack stream (signature mismatch).");
            }

            int pos = 0;
            byte flags = input[pos++];
            pos++; // 0xFB magic, already validated

            // Flags bit 0x80: 4-byte size fields; otherwise 3-byte. Bit 0x01: a
            // compressed-size field precedes the decompressed-size field.
            int sizeBytes = (flags & 0x80) != 0 ? 4 : 3;
            if ((flags & 0x01) != 0)
            {
                pos += sizeBytes;
            }

            int decompressedSize = 0;
            for (int i = 0; i < sizeBytes; i++)
            {
                decompressedSize = (decompressedSize << 8) | input[pos++];
            }

            var output = new byte[decompressedSize];
            int outPos = 0;

            while (pos < input.Length)
            {
                byte b0 = input[pos++];
                int literal;
                int length;
                int offset;

                if (b0 < 0x80)
                {
                    byte b1 = input[pos++];
                    literal = b0 & 0x03;
                    length = ((b0 & 0x1C) >> 2) + 3;
                    offset = ((b0 & 0x60) << 3) + b1 + 1;
                }
                else if (b0 < 0xC0)
                {
                    byte b1 = input[pos++];
                    byte b2 = input[pos++];
                    literal = (b1 >> 6) & 0x03;
                    length = (b0 & 0x3F) + 4;
                    offset = ((b1 & 0x3F) << 8) + b2 + 1;
                }
                else if (b0 < 0xE0)
                {
                    byte b1 = input[pos++];
                    byte b2 = input[pos++];
                    byte b3 = input[pos++];
                    literal = b0 & 0x03;
                    length = ((b0 & 0x0C) << 6) + b3 + 5;
                    offset = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
                }
                else if (b0 < 0xFC)
                {
                    literal = ((b0 & 0x1F) + 1) << 2;
                    length = 0;
                    offset = 0;
                }
                else
                {
                    literal = b0 & 0x03;
                    Array.Copy(input, pos, output, outPos, literal);
                    outPos += literal;
                    break;
                }

                Array.Copy(input, pos, output, outPos, literal);
                pos += literal;
                outPos += literal;

                for (int i = 0; i < length; i++)
                {
                    output[outPos] = output[outPos - offset];
                    outPos++;
                }
            }

            if (outPos != decompressedSize)
            {
                return Result.Failure<byte[]>($"RefPack decode produced {outPos} byte(s) but the header declared {decompressedSize}.");
            }

            return Result.Success(output);
        }
        catch (Exception ex)
        {
            return Result.Failure<byte[]>($"RefPack decompression failed: {ex.Message}", ex);
        }
    }

    public static Result<byte[]> Compress(byte[] input)
    {
        if (input.Length >= (1 << 24))
        {
            return Result.Failure<byte[]>("RefPack's 3-byte size header cannot encode payloads of 16 MiB or more.");
        }

        using var output = new MemoryStream();
        output.WriteByte(0x10);
        output.WriteByte(0xFB);
        output.WriteByte((byte)(input.Length >> 16));
        output.WriteByte((byte)(input.Length >> 8));
        output.WriteByte((byte)input.Length);

        int pos = 0;
        while (input.Length - pos >= 4)
        {
            // A literal-run command carries a multiple of 4 bytes, at most 112.
            int run = Math.Min((input.Length - pos) & ~3, 112);
            output.WriteByte((byte)(0xE0 | ((run >> 2) - 1)));
            output.Write(input, pos, run);
            pos += run;
        }

        int tail = input.Length - pos;
        output.WriteByte((byte)(0xFC | tail));
        output.Write(input, pos, tail);

        return Result.Success(output.ToArray());
    }
}
