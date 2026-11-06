namespace NaxExplorer.Core;

/// <summary>The difference between two listings of the same folder, so the list can update rows instead of rebuilding.</summary>
public sealed record FolderChanges(List<FileEntry> Removed, List<FileEntry> Added, List<FileEntry> Changed)
{
    public bool IsEmpty => Removed.Count == 0 && Added.Count == 0 && Changed.Count == 0;

    public static FolderChanges Compute(IReadOnlyList<FileEntry> before, IReadOnlyList<FileEntry> after)
    {
        var old = new Dictionary<string, FileEntry>(before.Count, StringComparer.OrdinalIgnoreCase);
        foreach (FileEntry entry in before) old[entry.FullPath] = entry;
        var added = new List<FileEntry>();
        var changed = new List<FileEntry>();
        foreach (FileEntry entry in after)
        {
            if (!old.Remove(entry.FullPath, out FileEntry? previous)) added.Add(entry);
            else if (!previous.SameState(entry)) changed.Add(entry);
        }
        return new FolderChanges([.. old.Values], added, changed);
    }
}

public enum TransferProblem
{
    None,

    /// <summary>Nothing was chosen.</summary>
    Nothing,

    /// <summary>A folder would be copied or moved into itself or into one of its own subfolders.</summary>
    IntoItself,

    /// <summary>Every item would be moved to the folder it is already in.</summary>
    AlreadyThere,
}

/// <summary>Checks a copy or move before it is handed to Windows, so impossible requests never start.</summary>
public static class TransferCheck
{
    public static TransferProblem Check(IReadOnlyList<string> sources, string targetFolder, bool move)
    {
        if (sources.Count == 0) return TransferProblem.Nothing;
        bool allInTarget = true;
        foreach (string source in sources)
        {
            if (Directory.Exists(source) && PathTools.IsSameOrInside(targetFolder, source)) return TransferProblem.IntoItself;
            string? parent = Path.GetDirectoryName(source.TrimEnd('\\'));
            if (parent is null || !PathTools.SameLocation(parent, targetFolder)) allInTarget = false;
        }
        return move && allInTarget ? TransferProblem.AlreadyThere : TransferProblem.None;
    }
}
