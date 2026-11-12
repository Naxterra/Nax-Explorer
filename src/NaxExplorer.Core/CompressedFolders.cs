using System.IO.Compression;

namespace NaxExplorer.Core;

public static class CompressedFolders
{
    public static bool IsZip(string path) => Path.GetExtension(PathTools.ParsingName(path)).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    public static string Location(string path) => PathTools.ShellPlace(PathTools.ParsingName(path));

    /// <summary>ZIP children have shell identities, not filesystem paths. Other virtual places remain separate.</summary>
    public static bool IsEntry(string path) => TrySplit(path, out _, out string entry) && entry.Length > 0;

    public static bool TrySplit(string location, out string archive, out string entry)
    {
        archive = entry = "";
        if (!PathTools.IsShellPlace(location)) return false;
        string path = PathTools.ParsingName(location).Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path)) return false;
        int start = 0;
        while ((start = path.IndexOf(".zip", start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + 4;
            if (end == path.Length || path[end] == '\\')
            {
                archive = path[..end];
                entry = path[end..].Trim('\\').Replace('\\', '/');
                return true;
            }
            start = end;
        }
        return false;
    }

    /// <summary>Immediate children for conflict planning. Null means a file; an empty list means an empty folder.</summary>
    public static IReadOnlyList<string>? Children(string location)
    {
        if (!TrySplit(location, out string file, out string entry)) return null;
        using var zip = ZipFile.OpenRead(file);
        string prefix = entry.Length == 0 ? "" : entry + "/";
        var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool folder = entry.Length == 0;
        foreach (var item in zip.Entries)
        {
            string name = item.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            folder = true;
            string rest = name[prefix.Length..];
            if (rest.Length == 0) continue;
            string child = rest.Split('/')[0];
            // Do not hand traversal segments to the shell, including when checking a malformed archive's conflicts.
            if (child is "." or ".." || child.Contains(':')) throw new InvalidDataException("Invalid ZIP entry name.");
            children.Add(Location(file + "\\" + prefix.Replace('/', '\\') + child));
        }
        return folder ? children.ToList() : null;
    }

    /// <summary>A missing archive is not proof that a cut entry moved; callers keep the clipboard on read errors.</summary>
    public static bool EntryExists(string location)
    {
        if (!TrySplit(location, out string file, out string entry)) return false;
        using var zip = ZipFile.OpenRead(file);
        return zip.Entries.Any(item => item.FullName.Replace('\\', '/').TrimEnd('/').Equals(entry, StringComparison.OrdinalIgnoreCase)
            || item.FullName.Replace('\\', '/').StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase));
    }
}
