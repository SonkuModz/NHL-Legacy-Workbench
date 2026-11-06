namespace NhlWorkbench.Core.UniformReplacer;

/// <summary>The gameplay equipment categories this replacer currently covers. Deliberately a small,
/// closed set for now -- gloves/skates/sticks/shoulder pads are explicit future expansion, added as
/// new enum values plus a new <see cref="UniformCategoryDefinition"/> entry each, once their own
/// real folder/naming conventions are confirmed the same way pant/sock/goalie helmet were here.</summary>
public enum UniformCategory
{
    Pant,
    Sock,
    GoalieHelmet,
}
