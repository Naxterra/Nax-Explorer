namespace NaxExplorer.Core;

/// <summary>An active mutation claim released via <see cref="ActiveMutationTracker.End"/>.</summary>
public sealed class ActiveMutationLease
{
    internal ActiveMutationLease(string[] writePaths, string[] readPaths)
    {
        WritePaths = writePaths;
        ReadPaths = readPaths;
    }

    public string[] WritePaths { get; }
    public string[] ReadPaths { get; }
    public bool IsEmpty => WritePaths.Length == 0 && ReadPaths.Length == 0;
}

/// <summary>
/// Tracks filesystem, ZIP, and shell paths currently being copied, moved, renamed, or deleted across all windows.
/// Prevents overlapping destructive operations and colliding transfers while allowing safe concurrent operations (such
/// as moving a file upward into its parent folder or copying unrelated files into an ancestor of an active delete).
/// </summary>
public sealed class ActiveMutationTracker
{
    private readonly Lock _gate = new();
    private readonly List<ActiveMutationLease> _leases = [];

    /// <summary>
    /// True when <paramref name="path"/> overlaps any active write target (delete, move, rename, or transfer
    /// destination), used by panes to pause background folder-size scans and previews on mutating trees.
    /// </summary>
    public bool Overlaps(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        lock (_gate) return OverlapsAnyWriteLocked(path);
    }

    /// <summary>
    /// Atomically claims <paramref name="writePaths"/> (exclusively) and <paramref name="readPaths"/> (shared against
    /// other reads, exclusive against writes), while also verifying <paramref name="targetContainer"/> is not itself
    /// being deleted, moved, or renamed by another active lease.
    /// </summary>
    public bool TryBegin(
        IReadOnlyList<string> writePaths,
        out ActiveMutationLease? lease,
        IReadOnlyList<string>? readPaths = null,
        string? targetContainer = null)
    {
        string[] writes = writePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] reads = readPaths is null
            ? []
            : readPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (writes.Length == 0 && reads.Length == 0 && string.IsNullOrWhiteSpace(targetContainer))
        {
            lease = null;
            return true;
        }
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(targetContainer) && IsContainedInAnyWriteLocked(targetContainer))
            {
                lease = null;
                return false;
            }
            foreach (string write in writes)
            {
                if (OverlapsAnyWriteOrReadLocked(write))
                {
                    lease = null;
                    return false;
                }
            }
            foreach (string read in reads)
            {
                if (OverlapsAnyWriteLocked(read))
                {
                    lease = null;
                    return false;
                }
            }
            lease = new ActiveMutationLease(writes, reads);
            if (!lease.IsEmpty) _leases.Add(lease);
            return true;
        }
    }

    /// <summary>Releases a previously claimed lease.</summary>
    public void End(ActiveMutationLease? lease)
    {
        if (lease is null || lease.IsEmpty) return;
        lock (_gate) _leases.Remove(lease);
    }

    private bool OverlapsAnyWriteLocked(string path)
    {
        foreach (ActiveMutationLease existing in _leases)
        {
            foreach (string target in existing.WritePaths)
            {
                if (PathsOverlap(path, target)) return true;
            }
        }
        return false;
    }

    private bool OverlapsAnyWriteOrReadLocked(string path)
    {
        foreach (ActiveMutationLease existing in _leases)
        {
            foreach (string target in existing.WritePaths)
            {
                if (PathsOverlap(path, target)) return true;
            }
            foreach (string source in existing.ReadPaths)
            {
                if (PathsOverlap(path, source)) return true;
            }
        }
        return false;
    }

    private bool IsContainedInAnyWriteLocked(string containerPath)
    {
        foreach (ActiveMutationLease existing in _leases)
        {
            foreach (string target in existing.WritePaths)
            {
                if (IsContainedIn(containerPath, target)) return true;
            }
        }
        return false;
    }

    private static bool PathsOverlap(string a, string b)
    {
        bool aZip = CompressedFolders.TrySplit(a, out string aArchive, out _);
        bool bZip = CompressedFolders.TrySplit(b, out string bArchive, out _);
        if (aZip && bZip)
        {
            // Windows rewrites the entire ZIP container on any entry write, so any two writes or write/read on the same archive overlap.
            return PathTools.SameLocation(aArchive, bArchive);
        }
        if (aZip)
        {
            return !PathTools.IsShellPlace(b) && (PathTools.IsSameOrInside(aArchive, b) || PathTools.IsSameOrInside(b, aArchive));
        }
        if (bZip)
        {
            return !PathTools.IsShellPlace(a) && (PathTools.IsSameOrInside(bArchive, a) || PathTools.IsSameOrInside(a, bArchive));
        }
        if (PathTools.IsShellPlace(a) || PathTools.IsShellPlace(b))
        {
            return PathTools.SameLocation(a, b);
        }
        return PathTools.IsSameOrInside(a, b) || PathTools.IsSameOrInside(b, a);
    }

    private static bool IsContainedIn(string path, string container)
    {
        bool pZip = CompressedFolders.TrySplit(path, out string pArchive, out _);
        bool cZip = CompressedFolders.TrySplit(container, out string cArchive, out _);
        if (pZip && cZip)
        {
            return PathTools.SameLocation(pArchive, cArchive);
        }
        if (pZip)
        {
            return !PathTools.IsShellPlace(container) && PathTools.IsSameOrInside(pArchive, container);
        }
        if (cZip)
        {
            return false;
        }
        if (PathTools.IsShellPlace(path) || PathTools.IsShellPlace(container))
        {
            return PathTools.SameLocation(path, container);
        }
        return PathTools.IsSameOrInside(path, container);
    }
}
