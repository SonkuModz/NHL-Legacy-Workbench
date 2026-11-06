namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// A stage-relative target position for <see cref="AptPlacementEditor.ComputeAnchorDelta"/> --
/// the 9-position grid (4 corners, 4 edge midpoints, center) a user picks from instead of typing
/// a raw pixel delta. <see cref="TopLeft"/> is the natural "original" position for content
/// authored to start near the stage's top-left, but reverting to the TRUE original (undoing any
/// staged move entirely) is handled separately by the caller, not through this enum -- computing
/// a delta that merely snaps the current bounds to (0,0) is not the same as undoing an edit if
/// the real authored content didn't originally start exactly at (0,0).
/// </summary>
public enum AptAnchorPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}
