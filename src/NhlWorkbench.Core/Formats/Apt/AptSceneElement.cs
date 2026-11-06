namespace NhlWorkbench.Core.Formats.Apt;

/// <summary>
/// One piece of a composed scorebug/overlay scene -- the output of <see
/// cref="AptSceneComposer"/>. <see cref="X"/>/<see cref="Y"/> are the element's real
/// resolved stage position: the placement chain's cumulative PlaceObject translate
/// (see <see cref="AptFrameTableReader"/>/<see cref="AptPlaceObjectRecord"/>) composed
/// down from the root Movie through every nested Sprite, plus the character's own
/// small local offset (a Shape's authored X/Y, or an EditText's -2,-2 box padding).
/// <see cref="Width"/>/<see cref="Height"/> are the character's own authored box size
/// scaled by that same cumulative placement scale -- normalized so a negative raw
/// width/height (the authored corner being the opposite one, observed on real NHL
/// shapes) always comes out as a proper top-left-origin, positive-size rectangle.
/// <see cref="Depth"/> is the owning placement's stage depth, used only to order
/// elements back-to-front; it is not itself a coordinate.
/// <see cref="TintColor"/> is the owning placement's <see
/// cref="AptPlaceObjectRecord.ApproximateColor"/> -- see that property's remarks for
/// what it is and, importantly, is NOT confirmed to be. It applies to any kind (a
/// VectorRectangle uses it as its only color source since no material data exists;
/// <see cref="TextColor"/> remains EditText's own real, independently-confirmed color
/// and takes priority for Text elements).
/// </summary>
public sealed record AptSceneElement(
    AptSceneElementKind Kind,
    float X,
    float Y,
    float Width,
    float Height,
    int GeometryKey,
    string? BackingEntryName,
    uint TextColor,
    string Text,
    int Depth,
    uint TintColor);
