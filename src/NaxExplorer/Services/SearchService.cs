using NaxExplorer.Core;
using NaxExplorer.Search;

namespace NaxExplorer.Services;

/// <summary>How much of a folder the AI index covers.</summary>
internal enum Coverage
{
    None,

    /// <summary>The folder lies inside an indexed folder.</summary>
    Full,

    /// <summary>Some indexed folders lie inside this folder, so only those parts can be searched by meaning.</summary>
    Partial,
}

/// <summary>
/// The app-wide AI search: the index, the model host, the indexer and the hybrid search. Nothing runs, and nothing is
/// downloaded, until the user turns AI search on.
/// </summary>
internal sealed class SearchService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TaskScheduler _startupScheduler;
    private IndexStore? _store;
    private EmbedderHost? _host;
    private Indexer? _indexer;
    private HybridSearch? _search;
    private IReadOnlyList<string> _roots = [];
    private long _generation;
    private readonly HashSet<Task> _searches = [];
    private Task _shutdown = Task.CompletedTask;

    /// <summary>Wait for all retired workers before deleting or rebuilding the index.</summary>
    public Task ShutdownCompletion
    {
        get { lock (_gate) return _shutdown; }
    }

    internal SearchService(TaskScheduler? startupScheduler = null) =>
        _startupScheduler = startupScheduler ?? TaskScheduler.Default;

    /// <summary>Raised on a background thread whenever the indexer reports progress.</summary>
    public event Action<IndexerStatus>? StatusChanged;

    /// <summary>Raised on a background thread when AI search starts or stops.</summary>
    public event Action? AvailabilityChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _search is not null;
        }
    }

    public IndexerStatus? Status
    {
        get
        {
            lock (_gate) return _indexer?.Status;
        }
    }

    public string? Device
    {
        get
        {
            lock (_gate) return _host?.Device;
        }
    }

    public static string IndexPath => Path.Combine(DataFolders.Index, "index.db");

    /// <summary>Why AI search could not start, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts AI search in the background when it is turned on and the model is present.</summary>
    public void Start(AppSettings settings)
    {
        if (!settings.AiSearch || !EmbeddingModel.IsInstalled()) return;
        bool useGpu = settings.UseGpu;
        List<string> roots = settings.IndexedFolders.ToList();
        bool fullScreen = settings.PauseIndexingInFullScreen, battery = settings.PauseIndexingOnBattery;
        long generation;
        lock (_gate)
        {
            if (_search is not null) return;
            generation = ++_generation;
        }
        Task.Factory.StartNew(() =>
        {
            try
            {
                lock (_gate)
                {
                    // Stop or a later settings change may have overtaken this queued startup.
                    if (generation != _generation || _search is not null) return;
                    _store = IndexStore.Open(IndexPath, EmbeddingModel.Id);
                    _host = new EmbedderHost(() => new OnnxEmbedder(EmbeddingModel.ModelPath, EmbeddingModel.TokenizerPath, useGpu));
                    _indexer = new Indexer(_store, _host) { PauseInFullScreen = fullScreen, PauseOnBattery = battery };
                    _indexer.StatusChanged += status => StatusChanged?.Invoke(status);
                    _search = new HybridSearch(_store, _host);
                    _roots = roots;
                    _indexer.Start(roots);
                    LastError = null;
                }
                AvailabilityChanged?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashLog.Write(ex);
                lock (_gate)
                {
                    if (generation == _generation) LastError = ex.Message;
                }
                Stop(generation);
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _startupScheduler);
    }

    /// <summary>Applies changed settings: folders, pause rules, and a restart when the GPU choice changed.</summary>
    public void Apply(AppSettings settings, bool restart)
    {
        if (restart || !settings.AiSearch)
        {
            Stop();
            Start(settings);
            return;
        }
        lock (_gate)
        {
            if (_indexer is null)
            {
                Start(settings);
                return;
            }
            _indexer.PauseInFullScreen = settings.PauseIndexingInFullScreen;
            _indexer.PauseOnBattery = settings.PauseIndexingOnBattery;
            _roots = settings.IndexedFolders.ToList();
            _indexer.SetRoots(_roots);
        }
    }

    public void SetUserPaused(bool paused)
    {
        lock (_gate)
        {
            if (_indexer is null) return;
            _indexer.UserPaused = paused;
            _indexer.Nudge();
        }
    }

    public bool UserPaused
    {
        get
        {
            lock (_gate) return _indexer?.UserPaused ?? false;
        }
    }

    public void Rescan()
    {
        lock (_gate) _indexer?.Rescan();
    }

    public Coverage CoverageOf(string location)
    {
        lock (_gate)
        {
            if (_search is null || PathTools.IsThisPc(location)) return _search is null ? Coverage.None : Coverage.Partial;
            if (_roots.Any(root => PathTools.IsSameOrInside(location, root))) return Coverage.Full;
            if (_roots.Any(root => PathTools.IsSameOrInside(root, location))) return Coverage.Partial;
            return Coverage.None;
        }
    }

    /// <summary>
    /// Runs a smart search on a background thread. Returns an empty list when AI search is not running. Without
    /// <paramref name="meaning"/> only names and exact words are searched, which needs no model and answers at once.
    /// </summary>
    public Task<List<SmartHit>> SearchAsync(string query, string? scope, CancellationToken token, bool meaning = true)
    {
        lock (_gate)
        {
            HybridSearch? search = _search;
            if (search is null) return Task.FromResult(new List<SmartHit>());
            Task<List<SmartHit>> task = Task.Run(() => search.Search(query, scope is null || PathTools.IsThisPc(scope) ? null : scope, 500, token, meaning), token);
            _searches.Add(task);
            _ = task.ContinueWith(completed => { lock (_gate) _searches.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>True when the model is in memory, so a search by meaning answers without loading it first.</summary>
    public bool ModelLoaded
    {
        get
        {
            lock (_gate) return _host?.IsLoaded ?? false;
        }
    }

    /// <summary>Loads the model ahead of a search, so the first query does not wait for it.</summary>
    public void Warm()
    {
        EmbedderHost? host;
        lock (_gate) host = _host;
        if (host is null || host.IsLoaded) return;
        Task.Run(() =>
        {
            try
            {
                using EmbedderHost.Lease lease = host.Acquire();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashLog.Write(ex);
            }
        });
    }

    public void Stop() => Stop(null);

    private void Stop(long? expectedGeneration)
    {
        Indexer? indexer;
        EmbedderHost? host;
        IndexStore? store;
        Task[] searches;
        lock (_gate)
        {
            if (expectedGeneration is { } expected && expected != _generation) return;
            _generation++;
            indexer = _indexer;
            host = _host;
            store = _store;
            searches = _searches.ToArray();
            _searches.Clear();
            _indexer = null;
            _host = null;
            _store = null;
            _search = null;
        }
        indexer?.Dispose();
        // A native inference call may outlive the bounded indexer shutdown. Searches may also still hold the
        // old snapshot. Retain their resources until every old worker has finished, without blocking the UI.
        Task finished = Task.WhenAll(searches.Append(indexer?.Completion ?? Task.CompletedTask));
        void Release()
        {
            host?.Dispose();
            store?.Dispose();
        }
        Task released;
        if (finished.IsCompleted)
        {
            Release();
            released = Task.CompletedTask;
        }
        else released = finished.ContinueWith(_ => Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate) _shutdown = Task.WhenAll(_shutdown, released);
        AvailabilityChanged?.Invoke();
    }

    /// <summary>Removes the index, and the model too when <paramref name="model"/> is set. AI search must be stopped first.</summary>
    public static void DeleteData(bool model)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataFolders.Index)) Directory.Delete(DataFolders.Index, recursive: true);
        if (model) EmbeddingModel.Delete();
    }

    public void Dispose() => Stop();
}
