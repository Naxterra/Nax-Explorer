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
}
