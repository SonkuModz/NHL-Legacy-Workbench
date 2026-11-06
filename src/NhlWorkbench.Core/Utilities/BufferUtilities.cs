namespace NhlWorkbench.Core.Utilities;

/// <summary>Bounds-safe byte array slicing shared by the container readers -- a malformed offset/size never throws, it just truncates/zero-pads.</summary>
public static class BufferUtilities
{
    public static byte[] SafeSlice(byte[] data, int offset, int count)
    {
        var result = new byte[Math.Max(0, count)];
        if (offset < 0 || offset >= data.Length || count <= 0)
        {
            return result;
        }

        int available = Math.Min(count, data.Length - offset);
        Array.Copy(data, offset, result, 0, available);
        return result;
    }
}
