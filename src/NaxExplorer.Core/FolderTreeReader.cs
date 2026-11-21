using System.IO.Enumeration;

namespace NaxExplorer.Core;

/// <summary>One folder entry shown in the sidebar folder tree.</summary>
public sealed record FolderTreeItem(string Name, string FullPath, bool HasSubfolders);

/// <summary>
/// Reads child folders and ancestor chains for the sidebar folder tree without ever opening files, hydrating unfetched
/// cloud folders, following junctions into loops, or probing network shares ahead of demand.
/// </summary>
public static class FolderTreeReader
{
    /// <summary>
    /// Reads direct subfolders of <paramref name="folder"/> in natural sort order. Unfetched cloud folders and shell
    /// places return an empty list so expanding the tree never triggers cloud downloads.
    /// </summary>
    public static List<FolderTreeItem> ReadChildren(string folder, bool showHidden, bool showProtected, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || PathTools.IsShellPlace(folder)) return [];
        token.ThrowIfCancellationRequested();
        try
        {
            FileAttributes rootAttributes = File.GetAttributes(folder);
            if ((rootAttributes & FileAttributes.Directory) == 0 || CloudFiles.IsUnfetchedFolder(rootAttributes)) return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };
        var walk = new FileSystemEnumerable<(string FullPath, FileAttributes Attributes)>(
            folder,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                entry.IsDirectory && IsVisible(entry.Attributes, showHidden, showProtected),
        };

        var items = new List<FolderTreeItem>();
        try
        {
            foreach ((string fullPath, FileAttributes attributes) in walk)
            {
                token.ThrowIfCancellationRequested();
                string name = ShellNames.Known(fullPath);
                bool hasSubfolders = HasSubfolders(fullPath, attributes, showHidden, showProtected);
                items.Add(new FolderTreeItem(name, fullPath, hasSubfolders));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        items.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return items;
    }

    /// <summary>
    /// Reports whether <paramref name="folder"/> should display an expand chevron. Unfetched cloud folders and links
    /// are never probed; network paths report true lazily so parent expansion never blocks on the network.
    /// </summary>
    public static bool HasSubfolders(string folder, FileAttributes attributes, bool showHidden, bool showProtected)
    {
        if (string.IsNullOrWhiteSpace(folder) || PathTools.IsShellPlace(folder)) return false;
        if ((attributes & FileAttributes.Directory) == 0) return false;
        if (CloudFiles.IsUnfetchedFolder(attributes)) return false;
        if (FolderStats.IsNetworkPath(folder)) return true;
        if (CloudFiles.IsLink(folder, attributes)) return false;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };
        var probe = new FileSystemEnumerable<bool>(
            folder,
            (ref FileSystemEntry _) => true,
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                entry.IsDirectory && IsVisible(entry.Attributes, showHidden, showProtected),
        };
        try
        {
            foreach (bool _ in probe) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return false;
    }

    /// <summary>
    /// Resolves a pane location to the filesystem folder the tree can sync to, mapping ZIP archive views to the folder
    /// containing the archive and ignoring This PC.
    /// </summary>
    public static string? SyncTarget(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || PathTools.IsThisPc(location)) return null;
        if (CompressedFolders.TrySplit(location, out string archive, out _)) return Path.GetDirectoryName(archive);
        return location;
    }

    /// <summary>
    /// Picks the most specific tree root containing <paramref name="location"/>, preferring deeper cloud roots over a
    /// bare drive letter when both contain the target folder.
    /// </summary>
    public static string? BestRoot(IEnumerable<string> roots, string? location)
    {
        if (SyncTarget(location) is not { } target) return null;
        if (PathTools.IsShellPlace(target))
            return roots.FirstOrDefault(r => PathTools.SameLocation(r, target));
        string? best = null;
        int bestLength = -1;
        foreach (string root in roots)
        {
            if (PathTools.IsShellPlace(root) || !PathTools.IsSameOrInside(target, root)) continue;
            int length = root.TrimEnd('\\').Length;
            if (length > bestLength)
            {
                best = root;
                bestLength = length;
            }
        }
        return best;
    }

    /// <summary>
    /// Returns the ordered sequence of subdirectories strictly below <paramref name="rootPath"/> leading down to and
    /// including <paramref name="location"/>. Returns an empty list when the target equals the root or lies outside it.
    /// </summary>
    public static List<string> AncestorsUnder(string rootPath, string? location)
    {
        if (SyncTarget(location) is not { } target) return [];
        if (PathTools.IsShellPlace(rootPath) || PathTools.IsShellPlace(target)) return [];
        if (PathTools.SameLocation(rootPath, target) || !PathTools.IsSameOrInside(target, rootPath)) return [];

        string cleanRoot = rootPath.TrimEnd('\\');
        string cleanTarget = target.TrimEnd('\\');
        if (cleanTarget.Length <= cleanRoot.Length) return [];
        string remainder = cleanTarget[cleanRoot.Length..].TrimStart('\\');
        if (remainder.Length == 0) return [];

        string current = rootPath.Length == 3 && rootPath[1] == ':' && rootPath[2] == '\\' ? rootPath : cleanRoot;
        var result = new List<string>();
        foreach (string segment in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            result.Add(current);
        }
        return result;
    }

    private static bool IsVisible(FileAttributes attributes, bool showHidden, bool showProtected)
    {
        bool hidden = (attributes & FileAttributes.Hidden) != 0;
        bool system = (attributes & FileAttributes.System) != 0;
        if (hidden && system && !showProtected) return false;
        if (hidden && !showHidden) return false;
        return true;
    }
}
