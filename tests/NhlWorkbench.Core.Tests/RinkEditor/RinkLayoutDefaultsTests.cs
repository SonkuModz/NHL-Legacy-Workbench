using NhlWorkbench.Core.RinkEditor;
using Xunit;

namespace NhlWorkbench.Core.Tests.RinkEditor;

public class RinkLayoutDefaultsTests
{
    [Fact]
    public void Slots_ContainsExactlyFiveKinds()
    {
        Assert.Equal(5, RinkLayoutDefaults.Slots.Count);
        var expectedKinds = new HashSet<RinkSlotKind>
        {
            RinkSlotKind.CenterLogo, RinkSlotKind.TopLeft, RinkSlotKind.TopRight, RinkSlotKind.BottomLeft, RinkSlotKind.BottomRight,
        };
        Assert.Equal(expectedKinds, RinkLayoutDefaults.Slots.Select(s => s.Kind).ToHashSet());
    }

    [Fact]
    public void CenterLogo_IsCenteredExactlyOnTheRinkCenterIceDot()
    {
        RinkSlotLayout centerLogo = RinkLayoutDefaults.GetSlot(RinkSlotKind.CenterLogo);

        Assert.Equal(RinkLayoutDefaults.ReferenceImageWidth / 2.0, centerLogo.CenterX);
        Assert.Equal(RinkLayoutDefaults.ReferenceImageHeight / 2.0, centerLogo.CenterY);
    }

    [Theory]
    [InlineData(RinkSlotKind.CenterLogo)]
    [InlineData(RinkSlotKind.TopLeft)]
    [InlineData(RinkSlotKind.TopRight)]
    [InlineData(RinkSlotKind.BottomLeft)]
    [InlineData(RinkSlotKind.BottomRight)]
    public void RectInReferenceSpace_TopLeftDerivesFromCenterAndSize(RinkSlotKind kind)
    {
        RinkSlotLayout slot = RinkLayoutDefaults.GetSlot(kind);

        RinkRect rect = slot.RectInReferenceSpace;

        Assert.Equal(slot.CenterX - (slot.Width / 2), rect.Left);
        Assert.Equal(slot.CenterY - (slot.Height / 2), rect.Top);
        Assert.Equal(slot.Width, rect.Width);
        Assert.Equal(slot.Height, rect.Height);
    }

    [Fact]
    public void CornerSlots_ShareTheSameSize()
    {
        RinkSlotLayout topLeft = RinkLayoutDefaults.GetSlot(RinkSlotKind.TopLeft);
        RinkSlotLayout topRight = RinkLayoutDefaults.GetSlot(RinkSlotKind.TopRight);
        RinkSlotLayout bottomLeft = RinkLayoutDefaults.GetSlot(RinkSlotKind.BottomLeft);
        RinkSlotLayout bottomRight = RinkLayoutDefaults.GetSlot(RinkSlotKind.BottomRight);

        Assert.Equal(topLeft.Width, topRight.Width);
        Assert.Equal(topLeft.Width, bottomLeft.Width);
        Assert.Equal(topLeft.Width, bottomRight.Width);
        Assert.Equal(topLeft.Height, topRight.Height);
        Assert.Equal(topLeft.Height, bottomLeft.Height);
        Assert.Equal(topLeft.Height, bottomRight.Height);
    }

    [Fact]
    public void CornerSlots_AreOffsetAboveAndBelowCenterOnOppositeSides()
    {
        RinkSlotLayout centerLogo = RinkLayoutDefaults.GetSlot(RinkSlotKind.CenterLogo);
        RinkSlotLayout topLeft = RinkLayoutDefaults.GetSlot(RinkSlotKind.TopLeft);
        RinkSlotLayout topRight = RinkLayoutDefaults.GetSlot(RinkSlotKind.TopRight);
        RinkSlotLayout bottomLeft = RinkLayoutDefaults.GetSlot(RinkSlotKind.BottomLeft);
        RinkSlotLayout bottomRight = RinkLayoutDefaults.GetSlot(RinkSlotKind.BottomRight);

        Assert.True(topLeft.CenterX < centerLogo.CenterX);
        Assert.True(topLeft.CenterY < centerLogo.CenterY);
        Assert.True(topRight.CenterX > centerLogo.CenterX);
        Assert.True(topRight.CenterY < centerLogo.CenterY);
        Assert.True(bottomLeft.CenterX < centerLogo.CenterX);
        Assert.True(bottomLeft.CenterY > centerLogo.CenterY);
        Assert.True(bottomRight.CenterX > centerLogo.CenterX);
        Assert.True(bottomRight.CenterY > centerLogo.CenterY);
    }
}
