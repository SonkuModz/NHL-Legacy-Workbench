namespace NhlWorkbench.Core.RinkEditor;

/// <summary>
/// Fixed positions/sizes for the Rink Editor's 5 slots, in the reference rink image's own pixel space
/// (<c>RinkeditorImage.png</c>, 5000x2236). Derived by pixel-diffing that blank template against a
/// real reference composite (<c>rinkeditorexample.png</c>) showing a populated center-ice area: the
/// center logo sits centered on the rink's center-ice dot, and the four sponsor hotspots sit just
/// above/below it, split left/right of center. Corner slots share one size constant rather than each
/// matching their specific example logo's bounding box exactly, since real replacement logos vary in
/// native aspect ratio and are scaled-to-fit within a consistent slot instead.
/// </summary>
public static class RinkLayoutDefaults
{
    public const int ReferenceImageWidth = 5000;
    public const int ReferenceImageHeight = 2236;

    private const double RinkCenterX = 2500;
    private const double RinkCenterY = 1118;

    private const double CenterLogoDiameter = 880;

    private const double CornerSlotWidth = 380;
    private const double CornerSlotHeight = 260;

    public static IReadOnlyList<RinkSlotLayout> Slots { get; } = new[]
    {
        new RinkSlotLayout(RinkSlotKind.CenterLogo, RinkCenterX, RinkCenterY, CenterLogoDiameter, CenterLogoDiameter),
        new RinkSlotLayout(RinkSlotKind.TopLeft, RinkCenterX - 224, RinkCenterY - 619, CornerSlotWidth, CornerSlotHeight),
        new RinkSlotLayout(RinkSlotKind.TopRight, RinkCenterX + 244, RinkCenterY - 618, CornerSlotWidth, CornerSlotHeight),
        new RinkSlotLayout(RinkSlotKind.BottomLeft, RinkCenterX - 250, RinkCenterY + 634, CornerSlotWidth, CornerSlotHeight),
        new RinkSlotLayout(RinkSlotKind.BottomRight, RinkCenterX + 234, RinkCenterY + 633, CornerSlotWidth, CornerSlotHeight),
    };

    public static RinkSlotLayout GetSlot(RinkSlotKind kind) => Slots.First(s => s.Kind == kind);
}
