namespace NaxExplorer.Services;

/// <summary>Opens a menu once. Commands that arrive after opening may only fill its reserved submenu.</summary>
internal static class ContextMenuPresentation
{
    internal static async Task ShowAsync<T>(
        Task<T> loading,
        TimeSpan patience,
        Func<bool> isCurrent,
        Action<T> showReady,
        Action showPending,
        Func<bool> isOpen,
        Action<T> fillSubmenu,
        Action<T> discard)
    {
        bool ready = await Task.WhenAny(loading, Task.Delay(patience)) == loading;
        if (!isCurrent())
        {
            discard(await loading);
            return;
        }
        if (ready)
        {
            showReady(await loading);
            return;
        }

        showPending();
        T result = await loading;
        if (isOpen()) fillSubmenu(result);
        else discard(result);
    }
}
