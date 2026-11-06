using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Mods;

/// <summary>One way a team's name is written, and every string that holds it.</summary>
public sealed record TeamNamePart(string Label, string Text, IReadOnlyList<string> Keys);

/// <summary>
/// A team's name isn't one string. The language files hold it as a full name, a city, a nickname, a short nickname and an
/// abbreviation, under keys built from the team's abbreviation and often again from its number.
/// </summary>
public static class TeamNames
{
    public const string FullName = "Full name";
    public const string City = "City";
    public const string Nickname = "Nickname";
    public const string ShortNickname = "Short nickname";
    public const string Abbreviation = "Abbreviation";

    /// <summary>The game's abbreviations all have three letters, and its key for them is called Abbr3.</summary>
    public const int LongestAbbreviation = 3;

    private static readonly (string Label, string[] ByAbbreviation, string[] ByNumber)[] Parts =
    [
        (FullName, ["NHLTeamName_{0}"], ["NHLTeamName_{0}"]),
        (City, ["NHLCityName_{0}", "TEAMLINE1_{0}"], ["NHLCityName_{0}", "CITYLINE1_{0}"]),
        (Nickname, ["TXT_NICKNAME_{0}", "TEAMLINE2_{0}", "NICKLINE2_{0}"], ["NICKLINE2_{0}"]),
        (ShortNickname, ["TXT_NICKNAME_ALT_{0}"], []),
        (Abbreviation, ["NHLTeamName_Abbr3_{0}", "X_XLA_TEAM_{0}"], ["NHLTeamName_Abbr3_{0}"]),
    ];

    /// <summary>The parts of a team's name this file has.</summary>
    /// <param name="abbreviation">The team's art abbreviation, such as PIT.</param>
    /// <param name="numbers">Numbers the team may also go by. One only counts where it holds the same name, so another team's string can't be taken by mistake.</param>
    public static IReadOnlyList<TeamNamePart> Read(TdbLanguageFile file, string abbreviation, IEnumerable<int> numbers)
    {
        ILookup<string, (string Key, string Text)> keys = file.Entries().Select(e => (e.Key, e.Text)).ToLookup(e => e.Key, StringComparer.OrdinalIgnoreCase);
        List<int> tried = numbers.Distinct().ToList();
        var parts = new List<TeamNamePart>();
        foreach ((string label, string[] byAbbreviation, string[] byNumber) in Parts)
        {
            List<(string Key, string Text)> found = byAbbreviation.SelectMany(p => keys[string.Format(p, abbreviation)]).ToList();
            if (found.Count == 0)
            {
                continue;
            }

            string text = found[0].Text;
            found.AddRange(byNumber
                .SelectMany(p => tried.SelectMany(n => keys[string.Format(p, n)]))
                .Where(e => Plain(e.Text) == Plain(text)));
            parts.Add(new TeamNamePart(label, text, found.Select(e => e.Key).Distinct().ToList()));
        }

        return parts;
    }

    /// <summary>Writes new text over the named parts. Returns how many strings changed, and any that couldn't be written.</summary>
    public static (int Changed, IReadOnlyList<string> Problems) Apply(TdbLanguageFile file, IReadOnlyList<TeamNamePart> parts, IReadOnlyDictionary<string, string> newText)
    {
        int changed = 0;
        var problems = new List<string>();
        foreach (TeamNamePart part in parts)
        {
            if (!newText.TryGetValue(part.Label, out string? text))
            {
                continue;
            }

            foreach (string key in part.Keys)
            {
                if (file.TryGetText(key, out string current) && current == text)
                {
                    continue;
                }

                if (file.TrySetText(key, text) is { } error)
                {
                    problems.Add($"{key}: {error}");
                }
                else
                {
                    changed++;
                }
            }
        }

        return (changed, problems);
    }

    /// <summary>Puts a team's strings back to how the game has them. Returns how many changed.</summary>
    public static int PutBack(TdbLanguageFile file, TdbLanguageFile original, IReadOnlyList<TeamNamePart> parts)
    {
        int changed = 0;
        foreach (string key in parts.SelectMany(p => p.Keys))
        {
            if (original.TryGetText(key, out string text) && file.TryGetText(key, out string current) && current != text && file.TrySetText(key, text) is null)
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>True if the two files have the same text throughout, so a mod doesn't need to carry this one.</summary>
    public static bool SameText(TdbLanguageFile file, TdbLanguageFile original) =>
        file.Entries().All(e => original.TryGetText(e.Key, out string text) && text == e.Text);

    private static string Plain(string text) => text.Replace("®", string.Empty).Replace("™", string.Empty).Trim().ToUpperInvariant();
}
