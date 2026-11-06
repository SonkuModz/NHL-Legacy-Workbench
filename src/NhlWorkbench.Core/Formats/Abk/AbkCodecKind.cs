namespace NhlWorkbench.Core.Formats.Abk;

/// <summary>
/// The per-sound audio codec ID stored in the S10A per-sound header's low nibble.
/// Values confirmed by reverse-engineering real NHL Legacy Edition .abk samples plus
/// EA's own SAGE-engine codec table (which uses the same company-wide ID scheme):
/// 3 = Xbox 360 XMA2, 4 = EA-XAS ADPCM. Other values are recognized names from that
/// same EA table but have not been observed in an actual NHL .abk file yet.
/// </summary>
public enum AbkCodecKind
{
    Unknown = -1,
    NoCompression = 2,
    Xma2 = 3,
    EaXas = 4,
}
