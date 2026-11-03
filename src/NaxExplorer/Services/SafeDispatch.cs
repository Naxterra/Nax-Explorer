using Microsoft.UI.Dispatching;

namespace NaxExplorer;

/// <summary>
/// Work queued to the UI thread is wrapped so that one failing callback is logged instead of ending the app. WinUI
/// does not pass exceptions from queued callbacks or timer ticks to Application.UnhandledException; they end the
/// process at once, which a file manager must not do because of a broken thumbnail or a vanished folder.
/// </summary>
internal static class SafeDispatch
{
    public static bool SafeEnqueue(this DispatcherQueue queue, Action action) =>
        queue.TryEnqueue(() => Run(action));

    public static bool SafeEnqueue(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action) =>
        queue.TryEnqueue(priority, () => Run(action));

    /// <summary>A timer tick handler that logs failures instead of ending the app.</summary>
    public static void SafeTick(this DispatcherQueueTimer timer, Action action) =>
        timer.Tick += (_, _) => Run(action);

    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex);
        }
    }
}

/// <summary>
/// The UI thread's synchronization context. Continuations of awaited tasks and exceptions from async void handlers
/// arrive here; a failure is logged and the app keeps running.
/// </summary>
internal sealed class SafeSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) =>
        queue.TryEnqueue(() => SafeDispatch.Run(() => callback(state)));

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (queue.HasThreadAccess)
        {
            SafeDispatch.Run(() => callback(state));
            return;
        }
        using var done = new ManualResetEventSlim();
        queue.TryEnqueue(() =>
        {
            SafeDispatch.Run(() => callback(state));
            done.Set();
        });
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => new SafeSynchronizationContext(queue);
}
