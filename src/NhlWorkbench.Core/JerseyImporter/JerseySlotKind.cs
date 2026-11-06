namespace NhlWorkbench.Core.JerseyImporter;

/// <summary>The 6 texture slots that make up one jersey "set", named after the destination RX2
/// container's own texture names rather than the source DDS suffix convention.</summary>
public enum JerseySlotKind
{
    FontCm,
    FontNm,
    FontSm,
    JerseyCm,
    JerseySm,
    JerseyNm,
}
