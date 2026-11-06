using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Utilities;

namespace NhlWorkbench.Core.Formats.Apt;

public static class AptConstantFileReader
{
    private const string Signature = "Apt constant file";
    private const int MaxItemCount = 100_000;

    public static bool IsAptConstantFile(byte[] data) =>
        data.Length >= Signature.Length
        && System.Text.Encoding.ASCII.GetString(data, 0, Signature.Length).Equals(Signature, StringComparison.Ordinal);

    public static Result<AptConstantFile> Parse(byte[] data)
    {
        try
        {
            if (!IsAptConstantFile(data))
            {
                return Result.Failure<AptConstantFile>("Not an Apt constant file (missing 'Apt constant file' signature).");
            }

            IReadOnlyList<(int Offset, string Value)> allStrings = AptStringScanner.ScanAll(data, 3);

            // Fixed-offset header fields (see AptConstantFile's remarks) -- degrade to an
            // empty item table rather than throwing if a future/unusual file is shorter
            // than expected, since AllStrings above already covers the safety-net case.
            uint aptDataOffset = 0, itemCount = 0, tablePointer = 0;
            var items = new List<AptConstantItem>();

            if (data.Length >= 0x20)
            {
                aptDataOffset = BigEndianReader.ReadUInt32BEAt(data, 0x14);
                itemCount = BigEndianReader.ReadUInt32BEAt(data, 0x18);
                tablePointer = BigEndianReader.ReadUInt32BEAt(data, 0x1C);

                if (itemCount <= MaxItemCount && tablePointer + (itemCount * 8) <= data.Length)
                {
                    for (int i = 0; i < itemCount; i++)
                    {
                        int entryOffset = (int)tablePointer + (i * 8);
                        uint type = BigEndianReader.ReadUInt32BEAt(data, entryOffset);
                        uint value = BigEndianReader.ReadUInt32BEAt(data, entryOffset + 4);
                        string? resolved = type == 1 && value < data.Length
                            ? AptStringScanner.ReadPrintableRun(data, (int)value)
                            : null;
                        items.Add(new AptConstantItem(i, type, value, resolved));
                    }
                }
            }

            return Result.Success(new AptConstantFile
            {
                AptDataOffset = aptDataOffset,
                ItemCount = itemCount,
                TablePointer = tablePointer,
                Items = items,
                AllStrings = allStrings,
                RawData = data,
            });
        }
        catch (Exception ex)
        {
            return Result.Failure<AptConstantFile>($"Unhandled error parsing Apt constant file: {ex.Message}", ex);
        }
    }
}
