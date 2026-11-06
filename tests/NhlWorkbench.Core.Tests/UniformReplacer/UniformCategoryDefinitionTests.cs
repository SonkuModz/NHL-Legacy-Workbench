using NhlWorkbench.Core.UniformReplacer;
using Xunit;

namespace NhlWorkbench.Core.Tests.UniformReplacer;

public class UniformCategoryDefinitionTests
{
    [Theory]
    [InlineData("", UniformSlotKind.ColorMap)]
    [InlineData("_2", UniformSlotKind.SpecularMap)]
    public void Pant_FromSourceSuffix_RecognizedSuffixes_MapToExpectedSlot(string suffix, UniformSlotKind expected)
    {
        Assert.Equal(expected, UniformCategoryDefinition.Pant.FromSourceSuffix(suffix));
    }

    /// <summary>Pant's Normal Map ("_1") is deliberately excluded -- confirmed by decoding both the
    /// real in-game normal map and the converted source that the source's channel data doesn't
    /// match (all detail in green, red near-zero, vs. the original's meaningful red+green), an
    /// upstream conversion-pipeline problem, not a container-format mismatch this importer can fix.
    /// See <c>UniformCategoryDefinition.Pant</c>'s doc comment.</summary>
    [Fact]
    public void Pant_FromSourceSuffix_NormalMapSuffix_DeliberatelyExcluded()
    {
        Assert.Null(UniformCategoryDefinition.Pant.FromSourceSuffix("_1"));
    }

    [Fact]
    public void Pant_FromSourceSuffix_UnrecognizedSuffix_ReturnsNull()
    {
        Assert.Null(UniformCategoryDefinition.Pant.FromSourceSuffix("_3"));
    }

    [Fact]
    public void Sock_FromSourceSuffix_OnlyColorMap()
    {
        Assert.Equal(UniformSlotKind.ColorMap, UniformCategoryDefinition.Sock.FromSourceSuffix(""));
        Assert.Null(UniformCategoryDefinition.Sock.FromSourceSuffix("_1"));
    }

    [Fact]
    public void GoalieHelmet_FromSourceSuffix_OnlyColorMap()
    {
        Assert.Equal(UniformSlotKind.ColorMap, UniformCategoryDefinition.GoalieHelmet.FromSourceSuffix(""));
        Assert.Null(UniformCategoryDefinition.GoalieHelmet.FromSourceSuffix("_1"));
    }

    [Theory]
    [InlineData("texlib_1_0_2", true)]
    [InlineData("texlib_0_0_2", false)]
    [InlineData("pant_0_0_0_0", false)]
    public void Pant_FolderNamePattern_MatchesOnlyExpectedShape(string folderName, bool expectedMatch)
    {
        Assert.Equal(expectedMatch, UniformCategoryDefinition.Pant.FolderNamePattern.IsMatch(folderName));
    }

    [Theory]
    [InlineData("sock_1_0_2_cm", true)]
    [InlineData("sock_0_0_2_cm", false)]
    [InlineData("playersock_0_0", false)]
    public void Sock_FolderNamePattern_MatchesOnlyExpectedShape(string folderName, bool expectedMatch)
    {
        Assert.Equal(expectedMatch, UniformCategoryDefinition.Sock.FolderNamePattern.IsMatch(folderName));
    }

    [Theory]
    [InlineData("goaliehelmet_0_0_cm", true)]
    [InlineData("cage_0_0_0", false)]
    [InlineData("throatprotector_0_0", false)]
    public void GoalieHelmet_FolderNamePattern_MatchesOnlyExpectedShape(string folderName, bool expectedMatch)
    {
        Assert.Equal(expectedMatch, UniformCategoryDefinition.GoalieHelmet.FolderNamePattern.IsMatch(folderName));
    }

    [Theory]
    [InlineData("pant_1_0_2_cm.Raster", UniformSlotKind.ColorMap)]
    [InlineData("pant_1_0_2_nm.Raster", UniformSlotKind.NormalMap)]
    [InlineData("pant_1_0_2_sm.Raster", UniformSlotKind.SpecularMap)]
    [InlineData("sock_1_0_2_cm.Raster", UniformSlotKind.ColorMap)]
    [InlineData("goaliehelmet_0_0_cm.Raster", UniformSlotKind.ColorMap)]
    public void SlotMapping_FromDestinationName_RecognizedNames(string name, UniformSlotKind expected)
    {
        Assert.Equal(expected, UniformSlotMapping.FromDestinationName(name));
    }

    [Fact]
    public void SlotMapping_FromDestinationName_UnrecognizedName_ReturnsNull()
    {
        Assert.Null(UniformSlotMapping.FromDestinationName("something_unrelated.Raster"));
    }
}
