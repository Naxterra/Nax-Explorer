namespace NaxExplorer.Services;

/// <summary>Opens one complete menu after its commands are ready, unless the request has been dismissed.</summary>
internal static class ContextMenuPresentation
{
    internal static async Task ShowAsync<T>(
        Task<T> loading,
        Func<bool> isCurrent,
        Action<T> showReady,
        Action<T> discard,
        CancellationToken token = default)
    {
        T result;
        try
        {
            result = await loading.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shell extensions cannot be interrupted safely. Release their result when it arrives, without keeping
            // the dismissed UI request waiting or ever opening a menu for it.
            _ = DiscardWhenReadyAsync(loading, discard);
            return;
        }
        if (token.IsCancellationRequested || !isCurrent())
        {
            discard(result);
            return;
        }
        showReady(result);
    }

    private static async Task DiscardWhenReadyAsync<T>(Task<T> loading, Action<T> discard)
    {
        try { discard(await loading.ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex); }
    }
}
