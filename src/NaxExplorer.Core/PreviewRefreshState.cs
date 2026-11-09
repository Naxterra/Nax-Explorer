namespace NaxExplorer.Core;

public sealed record PreviewEntry(FileEntry Entry, string Name, string FolderText = "");

/// <summary>An immutable copy of the data rendered by a preview, independent of list-row objects.</summary>
public sealed class PreviewRequest
{
    private readonly PreviewEntry[] _entries;
    private readonly HashSet<string> _paths;
    public string? Folder { get; }
    public int ImagePixels { get; }
    public double Scale { get; }
    public string? ContextName { get; }
    public bool CountsContent => _entries.Length > 1
        ? _entries.All(e => e.Entry.Kind != EntryKind.Drive && !PathTools.IsShellPlace(e.Entry.FullPath))
        : _entries.Length == 1 ? _entries[0].Entry.Kind == EntryKind.Folder && !PathTools.IsShellPlace(_entries[0].Entry.FullPath)
        : Folder is not null && !PathTools.IsThisPc(Folder) && !PathTools.IsShellPlace(Folder)
            && !PathTools.IsThisPc(PathTools.Parent(Folder) ?? "");

    public PreviewRequest(IEnumerable<PreviewEntry> entries, string? folder, int imagePixels, double scale, string? contextName = null)
    {
        _entries = entries.OrderBy(e => e.Entry.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        _paths = _entries.Select(e => e.Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Folder = folder;
        ImagePixels = imagePixels;
        Scale = scale;
        ContextName = contextName;
    }

    public bool SameSelection(IEnumerable<string> paths, int count, string? folder) =>
        StringComparer.OrdinalIgnoreCase.Equals(Folder, folder) && _entries.Length == count && paths.All(_paths.Contains);

    private bool SameTargets(PreviewRequest other)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Folder, other.Folder) || ContextName != other.ContextName || ImagePixels != other.ImagePixels || Scale != other.Scale || _entries.Length != other._entries.Length) return false;
        for (int i = 0; i < _entries.Length; i++)
        {
            var a = _entries[i];
            var b = other._entries[i];
            if (!PathTools.SameLocation(a.Entry.FullPath, b.Entry.FullPath) || a.Entry.Kind != b.Entry.Kind
                || a.Entry.Attributes != b.Entry.Attributes || a.Name != b.Name || a.FolderText != b.FolderText) return false;
        }
        return true;
    }

    public bool Matches(PreviewRequest other)
    {
        if (!SameTargets(other)) return false;
        for (int i = 0; i < _entries.Length; i++)
        {
            var a = _entries[i].Entry;
            var b = other._entries[i].Entry;
            if (!a.SameState(b) || a.CreatedUtc != b.CreatedUtc || a.TypeName != b.TypeName) return false;
        }
        return true;
    }

    public bool CanReuseTotals(PreviewRequest other) => CountsContent && other.CountsContent && SameTargets(other);

    public bool AffectedBy(string folder, FolderChanges? changes)
    {
        if (changes is not null)
            return changes.Added.Any(e => AffectedByEntry(e, false)) || changes.Changed.Any(e => AffectedByEntry(e, false))
                || changes.Removed.Any(e => AffectedByEntry(e, true));
        // An explicit operation knows its destination/source folders before their listings refresh.
        if (_entries.Length == 0) return Folder is { } root && PathTools.IsSameOrInside(folder, root);
        return _entries.Any(e => e.Entry.IsContainer
            ? PathTools.IsSameOrInside(folder, e.Entry.FullPath) || PathTools.SameLocation(PathTools.Parent(e.Entry.FullPath) ?? "", folder)
            : PathTools.SameLocation(PathTools.Parent(e.Entry.FullPath) ?? "", folder));
    }

    private bool AffectedByEntry(FileEntry changed, bool removed)
    {
        bool Affects(string path, bool container) => PathTools.SameLocation(path, changed.FullPath)
            || (container && PathTools.IsSameOrInside(changed.FullPath, path))
            || (removed && changed.IsContainer && PathTools.IsSameOrInside(path, changed.FullPath));
        if (_entries.Length == 0)
        {
            if (Folder is not { } root) return false;
            // A drive overview shows capacity, not a recursive listing of every changed file.
            if (PathTools.IsThisPc(PathTools.Parent(root) ?? "")) return changed.Kind == EntryKind.Drive && PathTools.SameLocation(root, changed.FullPath);
            return Affects(root, true);
        }
        return _entries.Any(e => Affects(e.Entry.FullPath, e.Entry.Kind == EntryKind.Folder));
    }
}

public enum PreviewRefreshAction { None, Reload, AfterCurrent }

/// <summary>Coalesces duplicate requests and lets live totals finish instead of continually cancelling them.</summary>
public sealed class PreviewRefreshState
{
    public PreviewRequest? Current { get; private set; }
    private long _generation;
    private bool _loading, _invalidated;

    public long? Begin(PreviewRequest request)
    {
        if (_loading && Current?.CanReuseTotals(request) == true)
        {
            _invalidated |= !Current.Matches(request);
            return null;
        }
        if (!_invalidated && Current?.Matches(request) == true) return null;
        Current = request;
        _loading = true;
        _invalidated = false;
        return ++_generation;
    }

    public bool Complete(long generation)
    {
        if (generation != _generation) return false;
        _loading = false;
        return _invalidated;
    }

    public void Cancel(bool pendingThumbnail = false)
    {
        _invalidated |= _loading || pendingThumbnail;
        _loading = false;
        _generation++;
    }

    public void Invalidate() => _invalidated = true;

    public PreviewRefreshAction Invalidate(string folder, FolderChanges? changes)
    {
        if (Current?.AffectedBy(folder, changes) != true) return PreviewRefreshAction.None;
        _invalidated = true;
        return _loading && Current.CountsContent ? PreviewRefreshAction.AfterCurrent : PreviewRefreshAction.Reload;
    }
}
