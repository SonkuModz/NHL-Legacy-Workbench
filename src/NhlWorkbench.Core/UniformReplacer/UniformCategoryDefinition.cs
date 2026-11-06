using System.Text.RegularExpressions;

namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>
/// Per-category naming rules, confirmed against the real source/destination trees (never guessed):
/// <list type="bullet">
/// <item>Pant: source folder/destination file stem "texlib_1_{teamId}_{variant}", 3 files
/// (base/_1/_2 = CM/NM/SM). Lives in the "pant" folder on both sides, alongside unrelated
/// "pant_*.rx2" mesh files that this pattern deliberately excludes.</item>
/// <item>Sock: stem "sock_1_{teamId}_{variant}_cm", exactly 1 file (CM only). Lives in the "sock"
/// folder alongside unrelated "playersock_*"/"goaliesock_*" files.</item>
/// <item>GoalieHelmet: stem "goaliehelmet_0_{id}_cm", exactly 1 file (CM only). Lives in the
/// "goaliehelmet" folder alongside unrelated "cage_*"/"helmet_*"/"throatprotector_*" files.</item>
/// </list>
/// </summary>
public sealed class UniformCategoryDefinition
{
    public UniformCategory Category { get; }

    /// <summary>Subfolder name under the source "renderinout" root and under the destination
    /// "rendering" root -- confirmed identical on both sides for every category so far.</summary>
    public string FolderName { get; }

    public Regex FolderNamePattern { get; }

    private readonly IReadOnlyDictionary<string, UniformSlotKind> _suffixToSlot;

    private UniformCategoryDefinition(UniformCategory category, string folderName, Regex folderNamePattern, IReadOnlyDictionary<string, UniformSlotKind> suffixToSlot)
    {
        Category = category;
        FolderName = folderName;
        FolderNamePattern = folderNamePattern;
        _suffixToSlot = suffixToSlot;
    }

    public UniformSlotKind? FromSourceSuffix(string suffix) => _suffixToSlot.TryGetValue(suffix, out UniformSlotKind kind) ? kind : null;

    /// <summary>
    /// Pant's "_1" (Normal Map) source file is deliberately excluded from <see cref="Pant"/>'s
    /// suffix map -- confirmed by decoding both the real, correct in-game normal map and the
    /// converted source DDS that a real pant's Normal Map channel data doesn't match: the game's
    /// own normal map has meaningful detail in both its red and green channels, while the
    /// converted source has it almost entirely in green with red near-zero. This isn't a
    /// container-format mismatch (that would be fixable by recompressing) -- it's an upstream
    /// problem in whatever produced the "2526 conversion" DDS output, so importing it would
    /// actively make pants look worse (confirmed: a real in-game screenshot showed a "puffy",
    /// wrong-looking texture) rather than better. Leave this excluded until that conversion
    /// pipeline is confirmed fixed, then restore the "_1" -> NormalMap mapping.
    /// </summary>
    public static readonly UniformCategoryDefinition Pant = new(
        UniformCategory.Pant,
        "pant",
        new Regex(@"^texlib_1_(\d+)_(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Dictionary<string, UniformSlotKind>
        {
            [""] = UniformSlotKind.ColorMap,
            ["_2"] = UniformSlotKind.SpecularMap,
        });

    public static readonly UniformCategoryDefinition Sock = new(
        UniformCategory.Sock,
        "sock",
        new Regex(@"^sock_1_(\d+)_(\d+)_cm$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Dictionary<string, UniformSlotKind> { [""] = UniformSlotKind.ColorMap });

    public static readonly UniformCategoryDefinition GoalieHelmet = new(
        UniformCategory.GoalieHelmet,
        "goaliehelmet",
        new Regex(@"^goaliehelmet_0_(\d+)_cm$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Dictionary<string, UniformSlotKind> { [""] = UniformSlotKind.ColorMap });

    public static IReadOnlyList<UniformCategoryDefinition> All { get; } = new[] { Pant, Sock, GoalieHelmet };
}
