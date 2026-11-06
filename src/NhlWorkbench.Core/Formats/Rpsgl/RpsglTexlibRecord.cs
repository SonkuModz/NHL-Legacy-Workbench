using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Rpsgl;

/// <summary>A single 48-byte texlib record.</summary>
internal readonly struct RpsglTexlibRecord
{
    public int DescriptorPointer { get; }

    public int CumulEnd { get; }

    private RpsglTexlibRecord(int descriptorPointer, int cumulEnd)
    {
        DescriptorPointer = descriptorPointer;
        CumulEnd = cumulEnd;
    }

    public static bool MagicMatches(byte[] data, int recordOffset)
    {
        for (int i = 0; i < RpsglLayout.TexlibRecordMagic.Length; i++)
        {
            if (data[recordOffset + i] != RpsglLayout.TexlibRecordMagic[i])
            {
                return false;
            }
        }

        return true;
    }

    public static RpsglTexlibRecord ReadAt(byte[] data, int recordOffset) =>
        new(
            BigEndianReader.ReadInt32BEAt(data, recordOffset + RpsglLayout.RecordDescriptorPointerOffset),
            BigEndianReader.ReadInt32BEAt(data, recordOffset + RpsglLayout.RecordCumulEndOffset));
}
