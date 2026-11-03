using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using NaxExplorer.Core;

namespace NaxExplorer.Search;

/// <summary>Which folders and files the index reads.</summary>
public static class IndexRules
{
    /// <summary>Plain text files larger than this are recorded by name only; they are usually logs or data dumps.</summary>
    public const long MaxPlainBytes = 20L * 1024 * 1024;

    /// <summary>Documents larger than this are recorded by name only.</summary>
    public const long MaxDocumentBytes = 100L * 1024 * 1024;

    /// <summary>At most this much text of one file is read.</summary>
    public const int MaxChars = 400_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", "__pycache__", ".venv", "venv", ".tox", ".cache",
        ".gradle", ".nuget", "$RECYCLE.BIN", "System Volume Information", ".mypy_cache", ".pytest_cache", ".next", ".angular",
    };

    public static bool SkipFolder(string name, FileAttributes attributes) =>
        SkippedFolders.Contains(name)
        || (attributes & FileAttributes.ReparsePoint) != 0
        || (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public static bool SkipFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) != 0
        // Cloud placeholders that are not on the disk would be downloaded just to be read.
        || (attributes & (FileAttributes)0x00400000 /* FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS */) != 0
        || (attributes & FileAttributes.Offline) != 0;

    /// <summary>True when the text of the file is read; otherwise only its name is recorded.</summary>
    public static bool ReadsText(string path, long size)
    {
        TextFormat format = TextExtractor.FormatOf(System.IO.Path.GetExtension(path));
        return format switch
        {
            TextFormat.None => false,
            TextFormat.Plain or TextFormat.Html or TextFormat.Rtf => size <= MaxPlainBytes,
            _ => size <= MaxDocumentBytes,
        };
    }
}

/// <summary>Tells whether background work should wait, so indexing never gets in the way of a game or a presentation.</summary>
public static partial class SystemActivity
{
    public enum Reason
    {
        None,
        FullScreen,
        Battery,
    }

    public static Reason ShouldPause(bool inFullScreen, bool onBattery)
    {
        if (inFullScreen && SHQueryUserNotificationState(out int state) == 0 && FullScreenPause(state, ForegroundCoversMonitor)) return Reason.FullScreen;
        if (onBattery && GetSystemPowerStatus(out PowerStatus power) && power.ACLineStatus == 0) return Reason.Battery;
        return Reason.None;
    }

    /// <summary>
    /// Whether a notification state means a game or presentation is running. Exclusive full-screen programs (3) and
    /// presentation mode (4) always pause. "Busy" (2) is also reported for overlays and other states, so it pauses only
    /// when the window in front really covers its whole monitor, as a borderless full-screen game does.
    /// </summary>
    internal static bool FullScreenPause(int state, Func<bool> foregroundCoversMonitor) => state switch
    {
        3 or 4 => true,
        2 => foregroundCoversMonitor(),
        _ => false,
    };

    private static bool ForegroundCoversMonitor()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out Rect bounds)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */), ref info)) return false;
        Rect monitor = info.Monitor;
        return bounds.Left <= monitor.Left && bounds.Top <= monitor.Top && bounds.Right >= monitor.Right && bounds.Bottom >= monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Lowers the processor, disk and memory priority of the calling thread.</summary>
    public static void EnterBackgroundMode() => SetThreadPriority(GetCurrentThread(), 0x00010000);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);
}

/// <summary>
/// Owns the loaded model. It is loaded on first use and released after a few idle minutes, so the graphics memory and
/// RAM it needs are only taken while indexing or searching. Callers hold a lease while they use it.
/// </summary>
public sealed class EmbedderHost : IDisposable
{
    private readonly Func<IEmbedder> _factory;
    private readonly TimeSpan _idleTime;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private IEmbedder? _embedder;
    private int _leases;
    private DateTime _lastUse;
    private bool _disposed;

    public EmbedderHost(Func<IEmbedder> factory, TimeSpan? idleTime = null)
    {
        _factory = factory;
        _idleTime = idleTime ?? TimeSpan.FromMinutes(4);
        _timer = new Timer(_ => ReleaseIfIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Where the model runs, or null while it is not loaded.</summary>
    public string? Device
    {
        get
        {
            lock (_gate) return _embedder?.Device;
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate) return _embedder is not null;
        }
    }

    /// <summary>Loads the model when needed. Dispose the lease when done; the model stays loaded until it is idle.</summary>
    public Lease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _embedder ??= _factory();
            _leases++;
            _lastUse = DateTime.UtcNow;
            return new Lease(this, _embedder);
        }
    }

    private void Return()
    {
        lock (_gate)
        {
            _leases--;
            _lastUse = DateTime.UtcNow;
        }
    }

    private void ReleaseIfIdle()
    {
        lock (_gate)
        {
            if (_embedder is null || _leases > 0 || DateTime.UtcNow - _lastUse < _idleTime) return;
            _embedder.Dispose();
            _embedder = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            if (_leases == 0)
            {
                _embedder?.Dispose();
                _embedder = null;
            }
        }
    }

    public sealed class Lease(EmbedderHost host, IEmbedder embedder) : IDisposable
    {
        private int _returned;

        public IEmbedder Embedder { get; } = embedder;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0) host.Return();
        }
    }
}

public enum IndexerPhase
{
    Starting,
    Scanning,
    Indexing,
    Paused,
    Idle,
    Failed,
}

public sealed record IndexerStatus(IndexerPhase Phase, int Documents, int Files, int Queued, string? Current, string? Detail, string? Device);

/// <summary>
/// Keeps the index in step with the indexed folders. A scan compares the folders with the index and queues new and
/// changed files; folder watchers queue later changes. One background thread with lowered priority reads the text,
/// splits it into pieces, computes their vectors in batches and saves each file in one transaction. It pauses while a
/// full-screen program such as a game runs and, if set, while the PC runs on battery. It never changes the user's files.
/// </summary>
public sealed class Indexer : IDisposable
{
    private const int BatchChunks = 48;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly IndexStore _store;
    private readonly EmbedderHost _host;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentDictionary<string, DateTime> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<WorkItem> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private volatile bool _stopping;
    private volatile bool _rescan;
    private IReadOnlyList<string> _roots = [];
    private IndexerStatus _status = new(IndexerPhase.Starting, 0, 0, 0, null, null, null);
    private DateTime _lastReport;

    public bool PauseInFullScreen { get; set; } = true;

    public bool PauseOnBattery { get; set; } = true;

    /// <summary>Set by the user to stop indexing until it is cleared.</summary>
    public bool UserPaused { get; set; }

    public IndexerStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    /// <summary>Raised on the indexer's thread, at most twice a second, and always when the phase changes.</summary>
    public event Action<IndexerStatus>? StatusChanged;

    public Indexer(IndexStore store, EmbedderHost host)
    {
        _store = store;
        _host = host;
        _thread = new Thread(Run) { IsBackground = true, Name = "Nax-Explorer indexer", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Starts indexing the folders. Call <see cref="SetRoots"/> later to change them.</summary>
    public void Start(IReadOnlyList<string> roots)
    {
        SetRoots(roots);
        _thread.Start();
    }

    /// <summary>Changes the indexed folders. Files outside the new folders leave the index at the next scan.</summary>
    public void SetRoots(IReadOnlyList<string> roots)
    {
        lock (_gate) _roots = roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RestartWatchers();
        Rescan();
    }

    public void Rescan()
    {
        _rescan = true;
        _wake.Set();
    }

    /// <summary>Lets the indexer check its pause conditions and queue again at once.</summary>
    public void Nudge() => _wake.Set();

    private void RestartWatchers()
    {
        lock (_gate)
        {
            foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            foreach (string root in _roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += (_, e) => Changed(e.FullPath);
                    watcher.Changed += (_, e) => Changed(e.FullPath);
                    watcher.Deleted += (_, e) => Changed(e.FullPath);
                    watcher.Renamed += (_, e) =>
                    {
                        Changed(e.OldFullPath);
                        Changed(e.FullPath);
                    };
                    // Too many changes at once: a scan finds them all.
                    watcher.Error += (_, _) => Rescan();
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // Without a watcher the folder is still indexed by every scan.
                }
            }
        }
    }

    private void Changed(string path)
    {
        _changed[path] = DateTime.UtcNow;
        _wake.Set();
    }

    private void Run()
    {
        SystemActivity.EnterBackgroundMode();
        while (!_stopping)
        {
            try
            {
                if (_rescan)
                {
                    _rescan = false;
                    Scan();
                }
                TakeSettledChanges();
                if (QueueCount == 0)
                {
                    Report(IndexerPhase.Idle, null, null, force: _status.Phase != IndexerPhase.Idle);
                    _wake.WaitOne(TimeSpan.FromSeconds(_changed.IsEmpty ? 30 : 1));
                    continue;
                }
                SystemActivity.Reason reason = UserPaused ? SystemActivity.Reason.None : SystemActivity.ShouldPause(PauseInFullScreen, PauseOnBattery);
                if (UserPaused || reason != SystemActivity.Reason.None)
                {
                    Report(IndexerPhase.Paused, null, UserPaused ? "user" : reason == SystemActivity.Reason.FullScreen ? "fullscreen" : "battery", force: _status.Phase != IndexerPhase.Paused);
                    _wake.WaitOne(TimeSpan.FromSeconds(5));
                    continue;
                }
                IndexBatch();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Report(IndexerPhase.Failed, null, ex.Message, force: true);
                _wake.WaitOne(TimeSpan.FromSeconds(30));
            }
        }
    }

    private int QueueCount
    {
        get
        {
            lock (_gate) return _queue.Count;
        }
    }

    private void Enqueue(WorkItem item, bool first)
    {
        lock (_gate)
        {
            if (!_queued.Add(item.Path)) return;
            if (first) _queue.AddFirst(item);
            else _queue.AddLast(item);
        }
    }

    private WorkItem? Dequeue()
    {
        lock (_gate)
        {
            if (_queue.First is not { } node) return null;
            _queue.RemoveFirst();
            _queued.Remove(node.Value.Path);
            return node.Value;
        }
    }

    /// <summary>Compares the indexed folders with the index: queues new and changed documents and forgets what is gone.</summary>
    private void Scan()
    {
        IReadOnlyList<string> roots;
        lock (_gate) roots = _roots;
        Report(IndexerPhase.Scanning, null, null, force: true);
        Dictionary<string, KnownFile> known = _store.KnownFiles();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<WorkItem>();
        var names = new List<(string, long, long)>();
        foreach (string root in roots)
        {
            foreach (FileEntry entry in Walk(root))
            {
                if (_stopping || _rescan) return;
                seen.Add(entry.FullPath);
                Consider(entry, known.GetValueOrDefault(entry.FullPath), work, names);
                if (names.Count >= 2000)
                {
                    _store.SaveNamesOnly(names);
                    names.Clear();
                }
                if (seen.Count % 500 == 0) Report(IndexerPhase.Scanning, null, null);
            }
        }
        _store.SaveNamesOnly(names);
        _store.Remove(known.Where(k => !seen.Contains(k.Key)).Select(k => k.Value.Id).ToList());
        // The most recently changed documents are read first, since they are the ones most likely to be searched for.
        foreach (WorkItem item in work.OrderByDescending(w => w.Modified)) Enqueue(item, first: false);
        Report(IndexerPhase.Scanning, null, null, force: true);
    }

    private static void Consider(FileEntry entry, KnownFile? known, List<WorkItem> work, List<(string, long, long)> names)
    {
        long modified = entry.ModifiedUtc.Ticks;
        bool unchanged = known is not null && known.Size == entry.Size && known.Modified == modified;
        if (unchanged) return;
        if (IndexRules.ReadsText(entry.FullPath, entry.Size)) work.Add(new WorkItem(entry.FullPath, entry.Size, modified));
        else names.Add((entry.FullPath, entry.Size, modified));
    }

    private static IEnumerable<FileEntry> Walk(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        var entries = new FileSystemEnumerable<FileEntry>(root, DirectoryReader.ToEntry, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && !IndexRules.SkipFile(entry.Attributes),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IndexRules.SkipFolder(entry.FileName.ToString(), entry.Attributes),
        };
        IEnumerator<FileEntry> enumerator;
        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        using (enumerator)
        {
            while (true)
            {
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }
                yield return enumerator.Current;
            }
        }
    }

    /// <summary>Handles watcher events that have been quiet for a few seconds, so files are read after they are written.</summary>
    private void TakeSettledChanges()
    {
        if (_changed.IsEmpty) return;
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<string> roots;
        lock (_gate) roots = _roots;
        foreach (var (path, time) in _changed)
        {
            if (now - time < Settle) continue;
            if (!_changed.TryRemove(new KeyValuePair<string, DateTime>(path, time))) continue;
            if (!roots.Any(root => PathTools.IsSameOrInside(path, root))) continue;
            if (Directory.Exists(path))
            {
                // A new or renamed folder: everything in it is looked at.
                var names = new List<(string, long, long)>();
                var work = new List<WorkItem>();
                foreach (FileEntry entry in Walk(path)) Consider(entry, _store.Find(entry.FullPath), work, names);
                _store.SaveNamesOnly(names);
                foreach (WorkItem item in work) Enqueue(item, first: true);
                continue;
            }
            FileEntry? file = DirectoryReader.ReadOne(path);
            if (file is null || file.IsContainer || IndexRules.SkipFile(file.Attributes))
            {
                // Gone: the file itself, or a folder with everything that was in it.
                if (_store.Find(path) is not null) _store.Remove(path);
                else _store.RemoveUnder(path);
                continue;
            }
            var fileWork = new List<WorkItem>();
            var fileNames = new List<(string, long, long)>();
            Consider(file, _store.Find(file.FullPath), fileWork, fileNames);
            _store.SaveNamesOnly(fileNames);
            foreach (WorkItem item in fileWork) Enqueue(item, first: true);
        }
    }

    /// <summary>Reads a few documents, computes the vectors of all their pieces in one go and saves each document.</summary>
    private void IndexBatch()
    {
        var prepared = new List<(WorkItem Item, List<string> Chunks, string Title)>();
        int pieces = 0;
        while (pieces < BatchChunks && Dequeue() is { } item)
        {
            Report(IndexerPhase.Indexing, item.Path, null);
            string? text = TextExtractor.Extract(item.Path, IndexRules.MaxChars);
            if (text is null || string.IsNullOrWhiteSpace(text))
            {
                _store.Save(item.Path, item.Size, item.Modified, text is null ? FileState.Failed : FileState.NameOnly, []);
                continue;
            }
            List<string> chunks = Chunker.Split(text);
            string title = Path.GetFileNameWithoutExtension(item.Path);
            prepared.Add((item, chunks, title));
            pieces += chunks.Count;
        }
        if (prepared.Count == 0) return;

        var inputs = new List<string>(pieces);
        foreach (var (_, chunks, title) in prepared)
        {
            foreach (string chunk in chunks) inputs.Add("passage: " + title + "\n" + chunk);
        }
        float[][] vectors;
        using (EmbedderHost.Lease lease = _host.Acquire())
        {
            vectors = lease.Embedder.Embed(inputs);
        }
        int next = 0;
        foreach (var (item, chunks, title) in prepared)
        {
            var stored = new List<StoredChunk>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                // The file name is part of the first piece's text, so exact words also find it there.
                string body = i == 0 ? title + "\n" + chunks[i] : chunks[i];
                stored.Add(new StoredChunk(body, VectorMath.Quantize(vectors[next++])));
            }
            _store.Save(item.Path, item.Size, item.Modified, FileState.Indexed, stored);
        }
        Report(IndexerPhase.Indexing, prepared[^1].Item.Path, null, force: QueueCount == 0);
    }

    private void Report(IndexerPhase phase, string? current, string? detail, bool force = false)
    {
        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastReport < TimeSpan.FromMilliseconds(500)) return;
        _lastReport = now;
        var (files, documents, _) = _store.Counts();
        var status = new IndexerStatus(phase, documents, files, QueueCount, current, detail, _host.Device);
        lock (_gate) _status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(10));
        lock (_gate)
        {
            foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
        _wake.Dispose();
    }

    private sealed record WorkItem(string Path, long Size, long Modified);
}
