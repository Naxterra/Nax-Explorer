using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using NaxExplorer.Core;
using NaxExplorer.Services;

namespace NaxExplorer.Shell;

/// <summary>One place an Explorer window showed: the place and the items selected in it.</summary>
internal sealed record CaughtPlace(string? Location, IReadOnlyList<string> Selection);

/// <summary>
/// While the app opens folders instead of Explorer, some programs still start Explorer directly, for example with
/// "explorer.exe /select,file", and the taskbar's Explorer button does too. Windows offers no way to stop that, so
/// this watches for Explorer folder windows as they appear, reads the place and the selection of each of their tabs,
/// closes the window and hands the places to the app. Places the app leaves to Explorer, such as Control Panel, stay
/// where they are. It works through Windows' own list of open folder windows and an accessibility event, and never
/// reaches into Explorer's process.
/// </summary>
internal sealed class ExplorerWindowCatcher : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;

    private readonly DispatcherQueue _queue;
    private readonly Action<IReadOnlyList<CaughtPlace>> _open;
    private readonly WinEventProc _callback;
    private readonly HashSet<IntPtr> _handled = [];
    private IntPtr _hook;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    /// <param name="open">Called on the UI thread with the places of one caught window, in its tab order.</param>
    public ExplorerWindowCatcher(DispatcherQueue queue, Action<IReadOnlyList<CaughtPlace>> open)
    {
        _queue = queue;
        _open = open;
        _callback = OnEvent;
    }

    /// <summary>Starts watching. Must be called on the UI thread, whose message loop delivers the events.</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
        if (_hook == IntPtr.Zero) CrashLog.Write(new COMException("Watching for Explorer windows could not start.", Marshal.GetHRForLastWin32Error()));
    }

    public void Dispose()
    {
        _disposed = true;
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == IntPtr.Zero || !IsExplorerWindow(hwnd)) return;
        if (!_handled.Add(hwnd)) return;
        if (_handled.Count > 200) _handled.Clear();
        // Explorer lists the window with its place only a moment after showing it, so the place is read a little later.
        Catch(hwnd, attempt: 0);
    }

    private void Catch(IntPtr hwnd, int attempt)
    {
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(attempt == 0 ? 150 : 250);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            if (_disposed || !IsWindow(hwnd)) return;
            // Shell automation and network-path checks can wait on another process. Keep them off the UI thread.
            List<CaughtPlace>? places = await ShellFolderReader.RunOnSta(() => ReadPlaces(hwnd));
            if (_disposed || !IsWindow(hwnd)) return;
            if (places is null)
            {
                // Not listed yet: tried again for about three seconds, then left alone.
                if (attempt < 12) Catch(hwnd, attempt + 1);
                return;
            }
            if (places.Count == 0 || places.Any(p => p.Location is { } place && ShellPlaces.OnlyExplorerShows(place))) return;
            // A place the app cannot open itself, such as a view only Explorer has, stays in Explorer: the window is
            // closed only when every one of its places resolves to a place the app shows.
            var resolved = new List<CaughtPlace>();
            foreach (CaughtPlace place in places)
            {
                if (place.Location is not { } location || !PathTools.IsShellPlace(location))
                {
                    resolved.Add(place);
                    continue;
                }
                string? own;
                try
                {
                    own = await ShellFolderReader.ResolveAsync(PathTools.ParsingName(location));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
                {
                    CrashLog.Write(ex);
                    return;
                }
                if (own is null || ShellPlaces.OnlyExplorerShows(own)) return;
                resolved.Add(place with { Location = own });
            }
            if (_disposed || !IsWindow(hwnd)) return;
            PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            _open(resolved);
        };
        timer.Start();
    }

    /// <summary>Set when watching stopped; a catch still under way then leaves its window alone.</summary>
    private bool _disposed;

    /// <summary>
    /// The places of all tabs of an Explorer window, or null while Windows does not list the window yet. A place is a
    /// folder path, a shell place, This PC, or null for Explorer's Home, which the app answers with its start folders.
    /// </summary>
    private static List<CaughtPlace>? ReadPlaces(IntPtr hwnd)
    {
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            if (shell is null) return null;
            dynamic windows = shell.Windows();
            int count = windows.Count;
            var places = new List<CaughtPlace>();
            bool listed = false;
            for (int i = 0; i < count; i++)
            {
                dynamic? browser = null;
                bool matched = false;
                try
                {
                    browser = windows.Item(i);
                    if (browser is null || (IntPtr)Convert.ToInt64(browser.HWND) != hwnd) continue;
                    matched = true;
                    listed = true;
                    dynamic? view = browser.Document;
                    if (view is null) return null;
                    string parsing = (string)view.Folder.Self.Path;
                    var selection = new List<string>();
                    foreach (dynamic item in view.SelectedItems()) selection.Add((string)item.Path);
                    places.Add(new CaughtPlace(PlaceFor(parsing), selection));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    // An entry that is not an Explorer window, such as the app's own, or one that is closing.
                    // If one of this window's tabs could not be read, preserve the entire original window.
                    if (matched) return null;
                }
                finally
                {
                    if (browser is not null) Native.Release((object)browser);
                }
            }
            return listed ? places : null;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            CrashLog.Write(ex);
            return null;
        }
        finally
        {
            if (shell is not null) Native.Release((object)shell);
        }
    }

    /// <summary>The app's place for what an Explorer window showed.</summary>
    private static string? PlaceFor(string parsing)
    {
        switch (ShellPlaces.Classify(parsing))
        {
            case ShellPlace.Start:
                return null;
            case ShellPlace.ThisPc:
                return PathTools.ThisPc;
        }
        if (Directory.Exists(parsing)) return parsing;
        // Control Panel and other places the app leaves to Explorer are recognized by their name here.
        if (ShellPlaces.OnlyExplorerShows(parsing)) return parsing;
        return PathTools.ShellPlace(parsing);
    }

    /// <summary>An Explorer folder window: class CabinetWClass, owned by explorer.exe.</summary>
    private static bool IsExplorerWindow(IntPtr hwnd)
    {
        var name = new System.Text.StringBuilder(32);
        if (GetClassName(hwnd, name, name.Capacity) == 0 || name.ToString() != "CabinetWClass") return false;
        Native.GetWindowThreadProcessId(hwnd, out uint processId);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
