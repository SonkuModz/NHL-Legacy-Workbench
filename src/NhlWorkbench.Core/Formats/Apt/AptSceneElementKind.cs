namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>What one <see cref="AptSceneElement"/> is paintable as, given what's currently decoded.</summary>
public enum AptSceneElementKind
{
    /// <summary>A Shape whose geometry key resolves to a real Texture entry -- paint the actual XPR2 pixels.</summary>
    TexturedShape,

    /// <summary>A Shape whose geometry key resolves to a ShapeRecord, but its real vector geometry couldn't be decoded (see <see cref="AptShapeGeometry"/> -- only the common single-child rectangle case is decoded) or resolves to nothing. Rendered as a labeled outline placeholder, never a fabricated fill.</summary>
    VectorShape,

    /// <summary>A Shape whose geometry key resolves to a ShapeRecord AND whose real rectangle was decoded from its actual vertex data (<see cref="AptShapeGeometry.TryDecodeRectangle"/>) -- a real, confirmed shape, safe to paint as an actual filled rectangle at its real position/size rather than a generic placeholder. Still no confirmed fill color, so the fill itself remains an approximation.</summary>
    VectorRectangle,

    /// <summary>An EditText with real static content -- paint the real text at its real bounds/color/font size.</summary>
    Text,
}
