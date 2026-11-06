namespace NhlWorkbench.Core.RinkEditor;

/// <summary>Top-left-origin rectangle in reference-image pixel space -- a plain value type so Core has
/// no dependency on WPF's <c>System.Windows.Rect</c>; the App layer converts to WPF units at the UI edge.</summary>
public readonly record struct RinkRect(double Left, double Top, double Width, double Height);

/// <summary>One slot's fixed geometry in the rink reference image's pixel space (see
/// <see cref="RinkLayoutDefaults.ReferenceImageWidth"/>/<see cref="RinkLayoutDefaults.ReferenceImageHeight"/>).
/// Center-based since that's how the layout was derived (offsets from the rink's center-ice dot), with
/// <see cref="RectInReferenceSpace"/> computed for WPF's top-left-origin positioning.</summary>
public sealed record RinkSlotLayout(RinkSlotKind Kind, double CenterX, double CenterY, double Width, double Height)
{
    public RinkRect RectInReferenceSpace => new(CenterX - (Width / 2), CenterY - (Height / 2), Width, Height);
}
