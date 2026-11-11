using Windows.Storage;

namespace NaxExplorer.Core;

/// <summary>Prepares the storage-item format before Windows starts a native file drag.</summary>
public static class FileDragPayload
{
    public static async Task<IStorageItem[]> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellation = default)
    {
        var items = new IStorageItem[paths.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation }, async (index, token) =>
            {
                token.ThrowIfCancellationRequested();
                string path = paths[index];
                items[index] = Directory.Exists(path)
                    ? await StorageFolder.GetFolderFromPathAsync(path).AsTask(token).ConfigureAwait(false)
                    : await StorageFile.GetFileFromPathAsync(path).AsTask(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return items;
    }

    /// <summary>
    /// The storage items, if they can be resolved within <paramref name="budget"/>; null when that is not enough, for
    /// very many items or a slow network share. The caller then supplies them when the drop target asks, instead of
    /// holding the drag back. A missing or unreadable item still fails, so a drag never silently leaves one out.
    /// </summary>
    public static Task<IStorageItem[]?> ReadWithinAsync(IReadOnlyList<string> paths, TimeSpan budget) =>
        WithinAsync(token => ReadAsync(paths, token), budget);

    /// <summary>
    /// The result of <paramref name="work"/> if it finishes within <paramref name="budget"/>, otherwise null at the end
    /// of the budget. The answer never waits longer, even when the work is stuck in a call that ignores cancellation,
    /// such as a check on a stalled network share: it is cancelled and left to end in the background, where a late
    /// failure is observed and dropped. A failure within the budget is passed on.
    /// </summary>
    public static async Task<T?> WithinAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan budget) where T : class
    {
        var limit = new CancellationTokenSource();
        // On the thread pool, so even work that blocks before its first await cannot hold the caller.
        Task<T> running = Task.Run(() => work(limit.Token));
        _ = running.ContinueWith(task => { _ = task.Exception; limit.Dispose(); }, TaskScheduler.Default);
        if (await Task.WhenAny(running, Task.Delay(budget)).ConfigureAwait(false) != running)
        {
            try { limit.Cancel(); } catch (ObjectDisposedException) { }
            return null;
        }
        try { return await running.ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
    }
}
