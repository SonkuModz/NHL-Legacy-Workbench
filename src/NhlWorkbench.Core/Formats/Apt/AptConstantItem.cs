namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One entry of a CONST file's item table -- confirmed layout (2026-08-19, verified
/// against 3 real NHL overlay archives): 8-byte stride, {type:u32, value:u32}. For
/// type==1 (String), value is a byte offset within the CONST file to a
/// null-terminated string; every real entry sampled so far is type 1. The wider type
/// enumeration (Undef=0, String=1, Property=2, None=3, Register=4, Boolean=5,
/// Float=6, Integer=7, Lookup=8) is carried over from the external community
/// research this format's CONST layout was cross-checked against (see
/// References/aptresearch/NCAA14_APT_SPEC.md "CONST File &amp; Constant Pool") --
/// only type 1 has actually been observed and decoded in real NHL bytes.
/// </summary>
public sealed record AptConstantItem(int Index, uint Type, uint RawValue, string? ResolvedString);
