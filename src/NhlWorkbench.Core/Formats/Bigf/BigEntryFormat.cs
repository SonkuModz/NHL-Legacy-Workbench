namespace NhlWorkbench.Core.Formats.Bigf;

/// <summary>What a BIGF entry's raw bytes turned out to be, per <see cref="BigEntryClassifier"/>.</summary>
public enum BigEntryFormat
{
    Rx2,
    Rpsgl,
    Dds,

    /// <summary>An XPR2 packed-resource texture, either bare or RefPack-compressed (the form the front-end team-logo archives actually use).</summary>
    Xpr,

    /// <summary>Zero-size entry -- a section-label marker, not real data (see <see cref="BigfArchive"/>'s remarks).</summary>
    Marker,

    /// <summary>Real data that isn't a texture container this workbench understands (e.g. Scaleform/GFx UI bytecode).</summary>
    Unknown,
}
