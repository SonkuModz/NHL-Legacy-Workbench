using System.Text;
using NhlWorkbench.Core.Database;
using NhlWorkbench.Core.Diagnostics;
using NhlWorkbench.Core.GameFiles;
using NhlWorkbench.Core.Tdb;

namespace NhlWorkbench.Core.Mods;

/// <summary>One of the game's language files.</summary>
public sealed record GameLanguage(string Code, string Name, string GamePath);

/// <summary>What a text CSV did. Lines that couldn't be used are listed and the rest still go in.</summary>
public sealed record GameTextImport(int Changed, IReadOnlyList<string> Problems);

/// <summary>Finds the game's language files, and moves their text in and out of spreadsheets.</summary>
public static class GameTextSheet
{
    public const string TeamNamePrefix = "NHLTeamName_";

    private static readonly (string Code, string Name)[] Known =
    [
        ("eng_us", "English"), ("fre_fr", "French"), ("ger_de", "German"), ("swe_se", "Swedish"),
        ("fin_fi", "Finnish"), ("cze_cz", "Czech"), ("rus_ru", "Russian"), ("lng_lg", "Language keys"),
    ];

    /// <summary>The languages this game has, English first.</summary>
    public static IReadOnlyList<GameLanguage> Languages(GameFileIndex index) =>
        Known.Select(k => new GameLanguage(k.Code, k.Name, $"fe/loc/nhl_{k.Code}.db")).Where(l => index.Find(l.GamePath) is not null).ToList();

    /// <summary>How many strings hold this text.</summary>
    public static int CountMatches(TdbLanguageFile file, string find, bool matchCase) =>
        find.Length == 0 ? 0 : file.Entries().Count(e => e.Text.Contains(find, Comparison(matchCase)));

    /// <summary>Swaps text throughout a file. A string that can't be written is reported and left as it was.</summary>
    public static GameTextImport ReplaceAll(TdbLanguageFile file, string find, string with, bool matchCase)
    {
        int changed = 0;
        var problems = new List<string>();
        if (find.Length > 0)
        {
            foreach ((string key, string text) in file.Entries().ToList())
            {
                string replaced = text.Replace(find, with, Comparison(matchCase));
                if (replaced != text)
                {
                    if (file.TrySetText(key, replaced) is { } error)
                    {
                        problems.Add($"{key}: {error}");
                    }
                    else
                    {
                        changed++;
                    }
                }
            }
        }

        return new GameTextImport(changed, problems);
    }

    private static StringComparison Comparison(bool matchCase) => matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>A CSV with a Key and a Text column. Pass keys to save only those.</summary>
    public static string ToCsv(TdbLanguageFile file, ISet<string>? onlyKeys = null)
    {
        var text = new StringBuilder("Key,Text\r\n");
        foreach ((string key, string value) in file.Entries())
        {
            if (onlyKeys?.Contains(key) != false)
            {
                text.Append(CsvText.Quote(key)).Append(',').Append(CsvText.Quote(value)).Append("\r\n");
            }
        }

        return text.ToString();
    }

    /// <summary>Applies a CSV's differences to the file. A key the file doesn't have, or a character it can't write, is reported and skipped.</summary>
    public static Result<GameTextImport> Apply(TdbLanguageFile file, string csv)
    {
        List<string[]> rows = CsvText.Parse(csv);
        if (rows.Count == 0)
        {
            return Result.Failure<GameTextImport>("That file is empty.");
        }

        int keyColumn = Array.FindIndex(rows[0], c => c.Trim().Equals("Key", StringComparison.OrdinalIgnoreCase));
        int textColumn = Array.FindIndex(rows[0], c => c.Trim().Equals("Text", StringComparison.OrdinalIgnoreCase));
        if (keyColumn < 0 || textColumn < 0)
        {
            return Result.Failure<GameTextImport>("This file needs Key and Text columns. Export a text CSV to see the layout.");
        }

        int changed = 0;
        var problems = new List<string>();
        for (int line = 1; line < rows.Count; line++)
        {
            string[] row = rows[line];
            if (row.Length <= Math.Max(keyColumn, textColumn) || row[keyColumn].Trim().Length == 0)
            {
                continue;
            }

            string key = row[keyColumn].Trim();
            if (!file.TryGetText(key, out string current))
            {
                problems.Add($"Line {line + 1}: the game has no text called {key}.");
            }
            else if (current != row[textColumn])
            {
                if (file.TrySetText(key, row[textColumn]) is { } error)
                {
                    problems.Add($"Line {line + 1} ({key}): {error}");
                }
                else
                {
                    changed++;
                }
            }
        }

        return Result.Success(new GameTextImport(changed, problems));
    }
}
