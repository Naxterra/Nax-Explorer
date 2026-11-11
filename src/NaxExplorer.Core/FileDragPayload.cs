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
    public static async Task<IStorageItem[]?> ReadWithinAsync(IReadOnlyList<string> paths, TimeSpan budget)
    {
        using var limit = new CancellationTokenSource(budget);
        try { return await ReadAsync(paths, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (limit.IsCancellationRequested) { return null; }
    }
}
