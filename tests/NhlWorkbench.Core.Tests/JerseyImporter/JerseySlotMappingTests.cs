using NhlWorkbench.Core.JerseyImporter;
using Xunit;

namespace NhlWorkbench.Core.Tests.JerseyImporter;

public class JerseySlotMappingTests
{
    [Theory]
    [InlineData("", JerseySlotKind.FontCm)]
    [InlineData("_1", JerseySlotKind.FontNm)]
    [InlineData("_2", JerseySlotKind.FontSm)]
    [InlineData("_3", JerseySlotKind.JerseyCm)]
    [InlineData("_4", JerseySlotKind.JerseySm)]
    [InlineData("_5", JerseySlotKind.JerseyNm)]
    public void FromSourceSuffix_RecognizedSuffixes_MapToExpectedSlot(string suffix, JerseySlotKind expected)
    {
        Assert.Equal(expected, JerseySlotMapping.FromSourceSuffix(suffix));
    }

    [Fact]
    public void FromSourceSuffix_UnrecognizedSuffix_ReturnsNull()
    {
        Assert.Null(JerseySlotMapping.FromSourceSuffix("_6"));
    }

    [Theory]
    [InlineData("font_texlib_1_0_2_cm.Raster", JerseySlotKind.FontCm)]
    [InlineData("font_texlib_1_0_2_nm.Raster", JerseySlotKind.FontNm)]
    [InlineData("font_texlib_1_0_2_sm.Raster", JerseySlotKind.FontSm)]
    [InlineData("jersey_texlib_1_0_2_cm.Raster", JerseySlotKind.JerseyCm)]
    [InlineData("jersey_texlib_1_0_2_sm.Raster", JerseySlotKind.JerseySm)]
    [InlineData("jersey_texlib_1_0_2_0_nm.Raster", JerseySlotKind.JerseyNm)]
    public void FromDestinationName_RecognizedNames_MapToExpectedSlot(string name, JerseySlotKind expected)
    {
        Assert.Equal(expected, JerseySlotMapping.FromDestinationName(name));
    }

    [Fact]
    public void FromDestinationName_UnrecognizedName_ReturnsNull()
    {
        Assert.Null(JerseySlotMapping.FromDestinationName("something_unrelated.Raster"));
    }

    [Fact]
    public void FromDestinationName_EmptyName_ReturnsNull()
    {
        Assert.Null(JerseySlotMapping.FromDestinationName(string.Empty));
    }
}
