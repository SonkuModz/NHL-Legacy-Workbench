namespace NhlWorkbench.Core.Formats.Overlay;

/// <summary>What one entry inside an overlay .big archive turned out to be, per <see cref="OverlayEntryClassifier"/>. A superset of <see cref="Bigf.BigEntryFormat"/> that additionally recognizes the APT sub-formats an overlay archive carries alongside its textures.</summary>
public enum OverlayEntryKind
{
    /// <summary>An "Apt Data" movie -- the component's class identity and imports (see <see cref="Apt.AptMovie"/>).</summary>
    Movie,

    /// <summary>An "Apt constant file" -- the movie's string/value constant pool.</summary>
    ConstantFile,

    /// <summary>A small self-length-prefixed record, believed to be vector shape/mask geometry (see <see cref="Apt.AptShapeRecord"/>'s remarks on what is and isn't confirmed).</summary>
    ShapeRecord,

    /// <summary>An XPR2 pixel texture (bare or RefPack-compressed) -- fully decodable via <see cref="Xpr.XprContainer"/>.</summary>
    Texture,

    /// <summary>Zero-size section-label marker.</summary>
    Marker,

    /// <summary>Real data that doesn't match any recognized shape.</summary>
    Unknown,
}
