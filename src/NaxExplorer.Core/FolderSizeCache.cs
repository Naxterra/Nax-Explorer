namespace NaxExplorer.Core;

/// <summary>
/// The sizes of folders shown in the size column, kept for a few minutes, so going back to a folder or reloading it
/// does not count the same large folders again and keep the disk busy.
/// </summary>
public sealed class FolderSizeCache
{
    public static FolderSizeCache Shared { get; } = new(TimeSpan.FromMinutes(10));

    private const int KeptForgets = 512;

    private readonly TimeSpan _lifetime;
    private readonly Dictionary<string, (FolderSizeResult Size, DateTime When)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(long Stamp, string Path, bool WithContents)> _forgets = new();
    private readonly object _gate = new();
    private long _stamp;
    private long _oldestKeptStamp = 1;

    public FolderSizeCache(TimeSpan lifetime) => _lifetime = lifetime;

    /// <summary>
    /// A number that grows with every forget. A count takes it before it starts and hands it to
    /// <see cref="Remember"/>, which then drops the result when a forget since then touched that folder.
    /// </summary>
    public long Stamp
    {
        get { lock (_gate) return _stamp; }
    }

    /// <summary>A complete size counted within the lifetime.</summary>
    public bool TryGet(string path, DateTime nowUtc, out FolderSizeResult? size)
    {
        lock (_gate)
        {
            if (_sizes.TryGetValue(path, out var known) && nowUtc - known.When < _lifetime)
            {
                size = known.Size;
                return true;
            }
        }
        size = null;
        return false;
    }

    /// <summary>
    /// Keeps a counted size. <paramref name="countStartedAt"/> is the <see cref="Stamp"/> taken before the count
    /// started; when something inside the folder changed while it was being counted, the result is not kept.
    /// </summary>
    public void Remember(string path, FolderSizeResult? size, DateTime nowUtc, long countStartedAt)
    {
        // A count that failed or stopped short may have hit a passing problem, such as a locked folder; it is not
        // kept, so the next look counts again.
        if (size is null || size.IsPartial) return;
        lock (_gate)
        {
            // The forgets made since the count started are no longer all known, so whether one touched this folder
            // cannot be told; the result is not kept.
            if (countStartedAt + 1 < _oldestKeptStamp) return;
            foreach (var forget in _forgets)
            {
                if (forget.Stamp > countStartedAt && Touches(forget.Path, forget.WithContents, path)) return;
            }
            if (_sizes.Count > 2000) _sizes.Clear();
            _sizes[path] = (size, nowUtc);
        }
    }

    /// <summary>
    /// Something changed directly in <paramref name="path"/>, such as a file added or written there: the kept sizes
    /// of that folder and of every folder that contains it are wrong. The folders inside it are not touched.
    /// </summary>
    public void Forget(string path) => Record(path, withContents: false);

    /// <summary>
    /// <paramref name="path"/> itself was removed, renamed, replaced or recounted on request: besides the folder and
    /// the folders that contain it, everything kept for the folders inside it is dropped too.
    /// </summary>
    public void ForgetWithContents(string path) => Record(path, withContents: true);

    private void Record(string path, bool withContents)
    {
        lock (_gate)
        {
            _stamp++;
            _forgets.Enqueue((_stamp, path, withContents));
            while (_forgets.Count > KeptForgets) _oldestKeptStamp = _forgets.Dequeue().Stamp + 1;
            foreach (string known in _sizes.Keys.Where(k => Touches(path, withContents, k)).ToList()) _sizes.Remove(known);
        }
    }

    /// <summary>True when a change at <paramref name="changed"/> makes the size of <paramref name="folder"/> wrong.</summary>
    private static bool Touches(string changed, bool withContents, string folder) =>
        PathTools.IsSameOrInside(changed, folder) || (withContents && PathTools.IsSameOrInside(folder, changed));

    /// <summary>
    /// False for the folders right below a drive root, such as C:\Windows or C:\Users: counting them takes minutes and
    /// keeps the disk busy, so the size column leaves them out, as the details panel does for a whole drive.
    /// </summary>
    public static bool CountsIn(string shownFolder)
    {
        string? root = Path.GetPathRoot(shownFolder);
        return string.IsNullOrEmpty(root) || !PathTools.SameLocation(root, shownFolder);
    }
}
