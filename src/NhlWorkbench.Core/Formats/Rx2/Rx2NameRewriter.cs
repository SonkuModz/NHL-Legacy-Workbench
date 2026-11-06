using System.Text;
using NhlWorkbench.Core.Diagnostics;

namespace NhlWorkbench.Core.Formats.Rx2;

/// <summary>
/// Retargets the per-slot numbers inside an RX2's texture names (e.g. <c>jersey_1_0_2_cm.Raster</c>
/// -> <c>jersey_1_0_5_cm.Raster</c>) so a copied file answers to its new name. The game's own jersey
/// containers all embed their style/art/variant in their texture names, and a clone that keeps the
/// source's names loads as a file but renders pink (the new slot's textures are never found by name).
///
/// Names are NUL-terminated ASCII strings in the header region, so same-length edits patch bytes in
/// place and touch nothing else. Changing the length would shift every later offset, so that is refused.
/// </summary>
public static class Rx2NameRewriter
{
    /// <summary>
    /// Replaces every NUL-delimited name string containing <paramref name="oldToken"/> (e.g. "_1_0_2",
    /// not followed by another digit) with <paramref name="newToken"/>. Fails, changing nothing, if the
    /// tokens differ in length or no name contained the old token.
    /// </summary>
    public static Result<byte[]> Rewrite(byte[] data, string oldToken, string newToken)
    {
        if (oldToken.Length != newToken.Length)
        {
            return Result.Failure<byte[]>("The new numbers have a different number of digits, which would shift the name table; refusing to rewrite.");
        }

        byte[] oldBytes = Encoding.ASCII.GetBytes(oldToken);
        byte[] newBytes = Encoding.ASCII.GetBytes(newToken);
        var result = (byte[])data.Clone();
        int patched = 0;

        int index = 0;
        while ((index = result.AsSpan(index).IndexOf(oldBytes) is var rel and >= 0 ? index + rel : -1) >= 0)
        {
            int end = index + oldBytes.Length;
            bool followedByDigit = end < result.Length && result[end] is >= (byte)'0' and <= (byte)'9';
            if (!followedByDigit && IsInsideNulDelimitedName(result, index, end))
            {
                newBytes.CopyTo(result, index);
                patched++;
            }

            index = end;
        }

        return patched == 0
            ? Result.Failure<byte[]>($"No texture name in the file contains '{oldToken}'.")
            : Result.Success(result);
    }

    /// <summary>True when the match sits in a printable-ASCII run (max 160 chars) that ends at a NUL byte and starts after a non-printable one -- i.e. a real name string, not coincidental pixel data.</summary>
    private static bool IsInsideNulDelimitedName(byte[] data, int start, int end)
    {
        int left = start;
        while (left > 0 && data[left - 1] is >= 0x20 and <= 0x7E && start - left < 160)
        {
            left--;
        }

        int right = end;
        while (right < data.Length && data[right] is >= 0x20 and <= 0x7E && right - end < 160)
        {
            right++;
        }

        // The first name in the pool follows a length/flag byte (0x0B in real files), so any non-printable byte may start the run; the end must be a NUL.
        bool startsAfterNonPrintable = left == 0 || data[left - 1] is < 0x20 or >= 0x7F;
        bool endsAtNul = right >= data.Length || data[right] == 0;
        return startsAfterNonPrintable && endsAtNul;
    }
}
