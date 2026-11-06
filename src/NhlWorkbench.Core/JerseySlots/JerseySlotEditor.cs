using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.JerseySlots;

/// <summary>
/// Slot-level operations on the stock jersey table (<c>stockteamjerseys</c>, "xjgw"): which rows
/// belong to a team, adding a slot, and reordering. Conventions below were read off the real game
/// database, not from docs:
/// <list type="bullet">
/// <item>Rows are always sorted by (team id, jerseyindex), and each team's jerseyindex runs 0..n-1.</item>
/// <item>A slot's texture set is the file <c>texlib_{style}_{artid}_{variant}.rx2</c>, so
/// (style, artid, variant) must be unique -- it is the slot's real identity; jerseyindex is just list order.</item>
/// <item>Slots with name type 6 get their name from the language string
/// <c>Vintage_{style}_{artid}_{variant}</c>; types 0-5 use shared, team-independent strings.</item>
/// <item>New rows go into the table's own spare capacity (free rows are padded with 0xDB); a full table can't grow.</item>
/// </list>
/// </summary>
public static class JerseySlotEditor
{
    public const int CustomNameType = 6;
    public const int MaxIndexOrVariant = 31;

    public static string[] NameTypeLabels { get; } =
    {
        "Home", "Away", "Alternate", "2006-07 Home", "2006-07 Away", "2006-07 2nd Home", "Custom / vintage name",
    };

    public static string VintageKey(long style, long artId, long variant) => $"Vintage_{style}_{artId}_{variant}";

    public static string TexlibFileName(long style, long artId, long variant) => $"texlib_{style}_{artId}_{variant}.rx2";

    /// <summary>Every slot in the real database (both styles) has pants at <c>pant/texlib_{style}_{art}_{variant}.rx2</c>.</summary>
    public static string PantFileName(long style, long artId, long variant) => $"texlib_{style}_{artId}_{variant}.rx2";

    /// <summary>Every style-1 slot has socks at this name; only some style-0 slots do.</summary>
    public static string SockFileName(long style, long artId, long variant) => $"sock_{style}_{artId}_{variant}_cm.rx2";

    /// <summary>The small name/number-plate colour map the game loads for every slot.</summary>
    public static string NameplateFileName(long style, long artId, long variant) => $"name_{style}_{artId}_{variant}_cm.rx2";

    /// <summary>The front-end (menu) jersey picture; a BIGF with no names inside, so it copies verbatim.</summary>
    public static string MenuArtFileName(long style, long artId, long variant) => $"jersey_{style}_{artId}_{variant}.big";

    /// <summary>One file a slot owns: where it lives (relative to the game's _compiled folder), its name, and whether the
    /// texture names inside carry the slot numbers and so must be rewritten when the file is cloned.</summary>
    public sealed record SlotAsset(string Label, string RelativeFolder, string FileName, bool NamesCarrySlotNumbers);

    /// <summary>
    /// Every per-slot file the game asked for or that real slots always have (found from the game's own log of a
    /// cloned slot, then confirmed against all 703 real slots): jersey textures, name plate, menu art, pants, socks.
    /// </summary>
    public static IReadOnlyList<SlotAsset> AssetsFor(long style, long artId, long variant) => new[]
    {
        new SlotAsset("jersey textures", Path.Combine("rendering", "jersey"), TexlibFileName(style, artId, variant), true),
        new SlotAsset("name plate", Path.Combine("rendering", "jersey"), NameplateFileName(style, artId, variant), true),
        new SlotAsset("menu art", Path.Combine("fe", "ion", "artassets", "jerseys"), MenuArtFileName(style, artId, variant), false),
        new SlotAsset("pants", Path.Combine("rendering", "pant"), PantFileName(style, artId, variant), true),
        new SlotAsset("socks", Path.Combine("rendering", "sock"), SockFileName(style, artId, variant), true),
    };

    /// <summary>The number-token the names inside a slot's files carry, e.g. "_1_0_5".</summary>
    public static string NameToken(long style, long artId, long variant) => $"_{style}_{artId}_{variant}";

    /// <summary>Row indexes of one team's slots, in table (= jerseyindex) order.</summary>
    public static List<int> RowsForTeam(TdbTableView jerseys, long teamId)
    {
        var rows = new List<int>();
        for (int r = 0; r < jerseys.RowCount; r++)
        {
            if (jerseys.GetInt(r, "teamid") == teamId)
            {
                rows.Add(r);
            }
        }

        return rows;
    }

    /// <summary>Smallest variant number for (style, artid) that no row uses and <paramref name="alsoInUse"/> doesn't flag (e.g. an existing file).</summary>
    public static long? NextFreeVariant(TdbTableView jerseys, long style, long artId, Func<long, bool>? alsoInUse = null, long? sameDigitCountAs = null)
    {
        int digits = sameDigitCountAs is { } d ? d.ToString().Length : 0;
        var used = new HashSet<long>();
        for (int r = 0; r < jerseys.RowCount; r++)
        {
            if (jerseys.GetInt(r, "style") == style && jerseys.GetInt(r, "teamartid") == artId)
            {
                used.Add(jerseys.GetInt(r, "variant"));
            }
        }

        for (long v = 0; v <= MaxIndexOrVariant; v++)
        {
            if (digits != 0 && v.ToString().Length != digits)
            {
                continue; // names inside cloned files can only be rewritten in place if the digit count stays the same
            }

            if (!used.Contains(v) && alsoInUse?.Invoke(v) != true)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>
    /// Clones <paramref name="sourceRow"/> as a new slot at the end of its team's list: new
    /// jerseyindex, a free variant number (also used for the front-end variant), custom-name type, not default.
    /// Returns the new row index; on failure nothing is changed.
    /// </summary>
    public static Result<int> AddSlot(TdbTableView jerseys, int sourceRow, Func<long, long, long, bool>? variantFileExists = null)
    {
        long team = jerseys.GetInt(sourceRow, "teamid");
        long style = jerseys.GetInt(sourceRow, "style");
        long artId = jerseys.GetInt(sourceRow, "teamartid");

        List<int> teamRows = RowsForTeam(jerseys, team);
        long nextIndex = teamRows.Count == 0 ? 0 : teamRows.Max(r => jerseys.GetInt(r, "jerseyindex")) + 1;
        if (nextIndex > MaxIndexOrVariant)
        {
            return Result.Failure<int>($"This team already has the maximum of {MaxIndexOrVariant + 1} jersey slots.");
        }

        long sourceVariant = jerseys.GetInt(sourceRow, "variant");
        long? variant = NextFreeVariant(jerseys, style, artId, v => variantFileExists?.Invoke(style, artId, v) == true, sourceVariant);
        if (variant is null)
        {
            return Result.Failure<int>(
                $"No free {sourceVariant.ToString().Length}-digit variant number is left for this team's art. "
                + "Clone a slot whose variant has a different number of digits (names inside the copied files can only be rewritten in place).");
        }

        int position = teamRows.Count == 0 ? jerseys.RowCount : teamRows[^1] + 1;
        if (!jerseys.TryInsertRow(position, out string? error))
        {
            return Result.Failure<int>(error ?? "Couldn't insert a row.");
        }

        // Inserting at/after the source's position can shift the source down by one.
        int source = sourceRow >= position ? sourceRow + 1 : sourceRow;
        jerseys.CopyRow(source, position);
        string? failure = null;
        bool ok = jerseys.TrySetInt(position, "jerseyindex", nextIndex, out failure)
            && jerseys.TrySetInt(position, "variant", variant.Value, out failure)
            && jerseys.TrySetInt(position, "fevariant", variant.Value, out failure)
            && jerseys.TrySetInt(position, "isdefault", 0, out failure)
            && jerseys.TrySetInt(position, "locstring", CustomNameType, out failure);
        if (!ok)
        {
            return Result.Failure<int>(failure ?? "Couldn't fill in the new row.");
        }

        return Result.Success(position);
    }

    /// <summary>Moves a slot up (-1) or down (+1) within its team by exchanging it with its neighbour, keeping each position's jerseyindex.</summary>
    public static bool TryMove(TdbTableView jerseys, int row, int delta)
    {
        int other = row + delta;
        if (Math.Abs(delta) != 1 || other < 0 || other >= jerseys.RowCount
            || jerseys.GetInt(other, "teamid") != jerseys.GetInt(row, "teamid"))
        {
            return false;
        }

        jerseys.SwapRowsKeepingField(row, other, "jerseyindex");
        return true;
    }
}
