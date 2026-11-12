namespace NaxExplorer.Core;

/// <summary>What the user chose in the app's conflict window for items whose name already exists in the target.</summary>
public enum ConflictChoice
{
    /// <summary>Replace the existing files; folders of the same name are merged and the files in them replaced too.</summary>
    Replace,

    /// <summary>Keep every existing file; folders of the same name are merged and only what is missing is added.</summary>
    Skip,

    /// <summary>Keep both: the new item gets a free name such as "Name (2)".</summary>
    KeepBoth,
}

/// <summary>The items of a copy or move whose names already exist in the target folder.</summary>
/// <param name="Items">The source items whose name is taken in the target.</param>
/// <param name="FilesReplaced">How many existing files "replace" would overwrite, also inside folders that are merged.</param>
/// <param name="Capped">True when counting stopped at the limit, so there are at least <paramref name="FilesReplaced"/>.</param>
public sealed record TransferConflicts(IReadOnlyList<string> Items, int FilesReplaced, bool Capped)
{
    public bool Any => Items.Count > 0;
}

/// <summary>One run of the copy engine: items that all go to one folder with the same flags.</summary>
public sealed record TransferBatch(IReadOnlyList<string> Sources, string TargetFolder, uint Flags);

/// <summary>
/// Plans a copy or move. Conflicts are found by reading folder listings only, which never downloads a cloud file. The
/// plan then splits the work into batches so that each answer to a conflict reaches the copy engine as flags: only the
/// items the user chose to replace run with "replace without asking", and "skip" never hands an existing file's
/// name to the engine at all.
/// </summary>
public static class TransferPlan
{
    /// <summary>Finds the conflicts, counting the files "replace" would overwrite up to <paramref name="countLimit"/>.</summary>
    public static TransferConflicts FindConflicts(IReadOnlyList<string> sources, string target, int countLimit = 10000)
    {
        var items = new List<string>();
        int replaced = 0;
        bool capped = false;
        foreach (string source in Outside(sources, target))
        {
            string existing = Path.Combine(target, NameOf(source));
            if (!Path.Exists(existing)) continue;
            items.Add(source);
            if (capped) continue;
            replaced += CountReplaced(source, existing, countLimit - replaced);
            if (replaced >= countLimit)
            {
                replaced = countLimit;
                capped = true;
            }
        }
        return new TransferConflicts(items, replaced, capped);
    }

    /// <summary>
    /// The batches for a copy or move with the user's <paramref name="choice"/> for conflicts. A copy into the folder
    /// the items already live in always keeps both, as in Explorer; a move there does nothing.
    /// </summary>
    /// <remarks>
    /// <paramref name="choice"/> is null when the user was never asked because no conflict was found. An item whose
    /// name became taken since then runs without "replace", so Windows asks about it instead of overwriting it.
    /// </remarks>
    public static IReadOnlyList<TransferBatch> Plan(IReadOnlyList<string> sources, string target, bool move, ConflictChoice? choice,
        IReadOnlyList<string>? confirmedConflicts = null)
    {
        var batches = new List<TransferBatch>();
        if (!move)
        {
            var same = sources.Where(s => InFolder(s, target)).ToList();
            if (same.Count > 0) batches.Add(new TransferBatch(same, target, OperationFlags.ForTransfer(renameOnCollision: true)));
        }
        var plain = new List<string>();
        var conflicting = new List<string>();
        var late = new List<string>();
        HashSet<string>? confirmed = confirmedConflicts is null ? null : new(confirmedConflicts, StringComparer.OrdinalIgnoreCase);
        foreach (string source in Outside(sources, target))
        {
            if (!Path.Exists(Path.Combine(target, NameOf(source)))) plain.Add(source);
            else if (choice == ConflictChoice.Replace && confirmed is not null && !confirmed.Contains(source)) late.Add(source);
            else conflicting.Add(source);
        }
        if (late.Count > 0) batches.Add(new TransferBatch(late, target, OperationFlags.ForTransfer(renameOnCollision: false)));
        if (plain.Count > 0) batches.Add(new TransferBatch(plain, target, OperationFlags.ForTransfer(renameOnCollision: false)));
        if (conflicting.Count == 0) return batches;
        switch (choice)
        {
            case null:
                batches.Add(new TransferBatch(conflicting, target, OperationFlags.ForTransfer(renameOnCollision: false)));
                break;
            case ConflictChoice.Replace:
                batches.Add(new TransferBatch(conflicting, target, OperationFlags.ForTransfer(renameOnCollision: false, replace: true)));
                break;
            case ConflictChoice.KeepBoth:
                batches.Add(new TransferBatch(conflicting, target, OperationFlags.ForTransfer(renameOnCollision: true)));
                break;
            case ConflictChoice.Skip:
                var missing = new List<(string Source, string Folder)>();
                foreach (string source in conflicting) AddMissing(source, Path.Combine(target, NameOf(source)), missing);
                foreach (IGrouping<string, (string Source, string Folder)> group in missing.GroupBy(m => m.Folder, StringComparer.OrdinalIgnoreCase))
                {
                    batches.Add(new TransferBatch(group.Select(m => m.Source).ToList(), group.Key, OperationFlags.ForTransfer(renameOnCollision: false)));
                }
                break;
        }
        return batches;
    }

    /// <summary>
    /// For "skip": when both the source and the existing item are folders, what is missing inside goes in, folder by
    /// folder; any other pair keeps the existing item and leaves the source where it is.
    /// </summary>
    private static void AddMissing(string source, string existing, List<(string Source, string Folder)> missing)
    {
        if (!IsRealFolder(existing)) return;
        IEnumerable<string> children;
        try
        {
            if (SourceChildren(source) is not { } found) return;
            children = found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (string child in children)
        {
            string there = Path.Combine(existing, Path.GetFileName(child));
            if (!Path.Exists(there)) missing.Add((child, existing));
            else AddMissing(child, there, missing);
        }
    }

    /// <summary>How many files "replace" would overwrite when <paramref name="source"/> lands on <paramref name="existing"/>.</summary>
    private static int CountReplaced(string source, string existing, int limit)
    {
        if (limit <= 0) return 0;
        if (!IsRealFolder(existing)) return 1;
        int count = 0;
        try
        {
            if (SourceChildren(source) is not { } children) return 1;
            foreach (string child in children)
            {
                string there = Path.Combine(existing, Path.GetFileName(child));
                if (Path.Exists(there)) count += CountReplaced(child, there, limit - count);
                if (count >= limit) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What cannot be read is not counted; the window says "at least" through the cap only.
        }
        return count;
    }

    /// <summary>
    /// A folder that is not a symbolic link or junction, so walking into it stays inside the copied tree. Folders in
    /// sync folders such as OneDrive are reparse points too, but they have no link target and count as real folders.
    /// </summary>
    private static bool IsRealFolder(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string>? SourceChildren(string source) => CompressedFolders.IsEntry(source)
        ? CompressedFolders.Children(source)
        : IsRealFolder(source) ? Directory.EnumerateFileSystemEntries(source).ToList() : null;

    private static IEnumerable<string> Outside(IReadOnlyList<string> sources, string target) =>
        sources.Where(s => !InFolder(s, target));

    private static bool InFolder(string source, string folder) =>
        Path.GetDirectoryName(source.TrimEnd('\\')) is { } parent && PathTools.SameLocation(parent, folder);

    private static string NameOf(string source) => Path.GetFileName(source.TrimEnd('\\'));
}
