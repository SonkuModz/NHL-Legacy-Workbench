namespace NhlWorkbench.Core.Mods;

/// <summary>Rules for names that end up as folders in a recomp install.</summary>
public static class ModNames
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Makes a mod name safe, matching the recomp launcher's clean_name. Also replaces "=", which would
    /// break the launcher's settings file. Returns null if nothing usable is left.
    /// </summary>
    public static string? Clean(string name)
    {
        char[] chars = name.Select(c => "<>:\"/\\|?*=".Contains(c) || c < ' ' ? '_' : c).ToArray();
        string clean = new string(chars).Trim().Trim('.').Trim();
        return clean.Length == 0 || clean.All(c => c == '_') || IsReserved(clean) ? null : clean;
    }

    /// <summary>Returns why a normalized game path can't become a Windows file path, or null if it can.</summary>
    public static string? CheckGamePath(string path)
    {
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return "It has an empty or relative folder name.";
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return $"\"{segment}\" ends in a dot or space, which Windows removes.";
            }

            if (IsReserved(segment))
            {
                return $"\"{segment}\" is a reserved Windows name.";
            }

            if (segment.Any(c => "<>:\"\\|?*".Contains(c) || c < ' '))
            {
                return $"\"{segment}\" has a character Windows doesn't allow.";
            }
        }

        return null;
    }

    private static bool IsReserved(string name)
    {
        int dot = name.IndexOf('.');
        return ReservedDeviceNames.Contains(dot >= 0 ? name[..dot] : name);
    }
}
