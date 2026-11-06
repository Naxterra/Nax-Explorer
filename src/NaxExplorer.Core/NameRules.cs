namespace NaxExplorer.Core;

public enum NameProblem
{
    None,
    Empty,
    InvalidCharacters,
    ReservedName,
    EndsWithDotOrSpace,
    TooLong,
}

/// <summary>The rules Windows sets for file and folder names, and unused names for new items and copies.</summary>
public static class NameRules
{
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static NameProblem Check(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return NameProblem.Empty;
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(c => c < 32)) return NameProblem.InvalidCharacters;
        if (name.EndsWith('.') || name.EndsWith(' ')) return NameProblem.EndsWithDotOrSpace;
        if (name.Length > 255) return NameProblem.TooLong;
        // "CON" and "con.txt" are both reserved; Windows looks at the part before the first dot.
        string stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) return NameProblem.ReservedName;
        return NameProblem.None;
    }

    /// <summary>
    /// A name that is not taken yet: the wanted name itself, or the name with " (2)", " (3)" and so on before the
    /// extension. Folders never get an extension split off.
    /// </summary>
    public static string Unused(string wanted, bool isFolder, Func<string, bool> exists)
    {
        if (!exists(wanted)) return wanted;
        string stem = isFolder ? wanted : Path.GetFileNameWithoutExtension(wanted);
        string extension = isFolder ? "" : Path.GetExtension(wanted);
        for (int number = 2; number < 10000; number++)
        {
            string candidate = $"{stem} ({number}){extension}";
            if (!exists(candidate)) return candidate;
        }
        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }
}
