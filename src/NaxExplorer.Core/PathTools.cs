namespace NaxExplorer.Core;

/// <summary>One step of the address bar: the text it shows and the place it opens.</summary>
public sealed record PathSegment(string Label, string Location);

/// <summary>Paths and places the panes can show. A place is a folder path or <see cref="ThisPc"/>.</summary>
public static class PathTools
{
    /// <summary>The list of drives, which is not a folder on disk.</summary>
    public const string ThisPc = "::ThisPC";

    public static bool IsThisPc(string location) => string.Equals(location, ThisPc, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when both name the same place, ignoring case and a trailing backslash.</summary>
    public static bool SameLocation(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string path)
    {
        if (path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/'))) return path.TrimEnd('\\', '/');
        return path;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or lies anywhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string p = Trim(path), f = Trim(folder);
        if (string.Equals(p, f, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The place one level up: the parent folder, This PC for a drive root, or null for This PC.</summary>
    public static string? Parent(string location)
    {
        if (IsThisPc(location)) return null;
        string? parent = Path.GetDirectoryName(Trim(location));
        if (string.IsNullOrEmpty(parent)) return ThisPc;
        return parent;
    }

    /// <summary>
    /// Turns what the user typed into the address bar into a full path, or null when it is not a usable path. Quotes
    /// are removed, environment variables such as %USERPROFILE% are expanded, and relative paths start at the current
    /// folder. A bare drive letter such as "D:" means the root of that drive.
    /// </summary>
    public static string? Normalize(string input, string? currentFolder)
    {
        string text = input.Trim().Trim('"').Trim();
        if (text.Length == 0) return null;
        text = Environment.ExpandEnvironmentVariables(text);
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return char.ToUpperInvariant(text[0]) + ":\\";
        try
        {
            if (!Path.IsPathFullyQualified(text))
            {
                if (currentFolder is null || IsThisPc(currentFolder)) return null;
                text = Path.Combine(currentFolder, text);
            }
            string full = Path.GetFullPath(text);
            if (full.Length == 2 && full[1] == ':') full += "\\";
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The steps of the address bar for a place, starting with This PC: "C:\Users\a" gives This PC, "C:", "Users", "a".
    /// A network path starts with its "\\server\share" root.
    /// </summary>
    public static List<PathSegment> Segments(string location, string thisPcLabel)
    {
        var result = new List<PathSegment> { new(thisPcLabel, ThisPc) };
        if (IsThisPc(location)) return result;
        string path = Trim(location);
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return result;
        string rootLabel = root.TrimEnd('\\');
        string rootLocation = root.EndsWith('\\') ? root : root + "\\";
        result.Add(new PathSegment(rootLabel, rootLocation));
        string rest = path[Math.Min(root.Length, path.Length)..].Trim('\\');
        if (rest.Length == 0) return result;
        string current = rootLocation;
        foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            result.Add(new PathSegment(part, current));
        }
        return result;
    }

    /// <summary>The short name a tab shows for a place.</summary>
    public static string TabTitle(string location, string thisPcLabel)
    {
        if (IsThisPc(location)) return thisPcLabel;
        string trimmed = Trim(location);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed.TrimEnd('\\') : name;
    }
}
