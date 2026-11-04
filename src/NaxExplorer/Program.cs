using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NaxExplorer.Services;
using NaxExplorer.Shell;

namespace NaxExplorer;

public static class Program
{
    /// <summary>The app's identity on the taskbar. The installer's shortcuts carry the same value.</summary>
    internal const string AppUserModelId = "Naxterra.NaxExplorer";

    /// <summary>The folder or file named on the command line, if any.</summary>
    internal static string? StartPath { get; private set; }

    private static Mutex? s_running;
    private static Mutex? s_runningGlobal;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // While the app opens folders instead of Explorer, Windows also hands it places only Explorer can show, such
        // as the Recycle Bin, Control Panel or a computer under Network; they go straight to Explorer, before any
        // window opens. Should Explorer ever hand the same place straight back, the second round stops here.
        // The marker of an earlier hand-off is read once and then removed, so nothing this process starts inherits it.
        string? marker = Environment.GetEnvironmentVariable(Core.ShellPlaces.HandOffMarker);
        if (marker is not null) Environment.SetEnvironmentVariable(Core.ShellPlaces.HandOffMarker, null);
        if (StartArguments.ExplorerPlace(args) is { } place)
        {
            bool startedByOwnHandOff = Core.ShellPlaces.IsOwnHandOff(marker, place, DateTime.UtcNow);
            if (!startedByOwnHandOff && Core.ShellPlaces.MayHandOff(place, Core.DataFolders.HandOff, DateTime.UtcNow)) StartArguments.OpenInExplorer(place);
            else Native.MessageBox(IntPtr.Zero, Loc.F("HandOffLoop", place), "Nax Explorer", 0x30);
            return 0;
        }
        StartPath = StartArguments.LocationFrom(args);
        // One process serves every window: a second start hands its folder to the running app and ends.
        AppInstance key = AppInstance.FindOrRegisterForKey("Naxterra.NaxExplorer.Main");
        if (!key.IsCurrent)
        {
            Redirect(key);
            return 0;
        }
        // Held for the life of the process. Setup and the uninstaller wait while either exists, so they never meet locked
        // program files. The global one is also seen from another Windows session, for example after a user switch.
        s_running = new Mutex(false, "NaxExplorer.Running");
        try
        {
            s_runningGlobal = new Mutex(false, @"Global\NaxExplorer.Running");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Without it only the session's own setup sees the app, as before.
        }
        key.Activated += (_, activation) => App.Instance?.OnRedirected(activation);
        Native.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        Native.AllowDarkMenus();
        Application.Start(_ =>
        {
            var context = new SafeSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>
    /// Hands this start to the running instance. The call is made on a worker thread while this STA thread keeps
    /// pumping COM messages, which the redirection needs.
    /// </summary>
    private static void Redirect(AppInstance key)
    {
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Native.AllowSetForegroundWindow((int)key.ProcessId);
        IntPtr done = Native.CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            try
            {
                key.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                Native.SetEvent(done);
            }
        });
        Native.CoWaitForMultipleObjects(0, 15000, 1, [done], out _);
        Native.CloseHandle(done);
    }
}

/// <summary>Reads the folder to open from the command line.</summary>
internal static class StartArguments
{
    /// <summary>The place to show: a folder or file, This PC, or null for the start folders, as for Home and Win+E.</summary>
    public static string? LocationFrom(IEnumerable<string> args)
    {
        List<string> list = args.ToList();
        if (list.Any(a => Core.ShellPlaces.Classify(a) == Core.ShellPlace.ThisPc)) return Core.PathTools.ThisPc;
        return PathFrom(list);
    }

    /// <summary>The first argument that names a place only Explorer can show, or null.</summary>
    public static string? ExplorerPlace(IEnumerable<string> args) =>
        args.FirstOrDefault(a => Core.ShellPlaces.Route(a, Path.Exists, File.Exists) == Core.ShellPlace.Explorer);

    /// <summary>
    /// Opens a place in Explorer. Started with the place itself, Explorer shows it directly and does not ask the
    /// folder commands the app has taken over, so this never comes back to the app.
    /// </summary>
    public static void OpenInExplorer(string place)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(place.Trim().Trim('"'));
        // Inherited by anything this Explorer starts, so a start of the app for the same place is known as a loop.
        start.Environment[Core.ShellPlaces.HandOffMarker] = Core.ShellPlaces.MarkerValue(place, DateTime.UtcNow);
        try
        {
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>The first argument that names an existing folder or file, or null.</summary>
    public static string? PathFrom(IEnumerable<string> args)
    {
        foreach (string raw in args)
        {
            string arg = raw.Trim().Trim('"');
            if (arg.Length == 0 || arg.StartsWith('-') || arg.StartsWith('/')) continue;
            if (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(arg) && string.Equals(Path.GetFileName(arg), "Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? full = Core.PathTools.Normalize(arg, Environment.CurrentDirectory);
            if (full is not null && Path.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>Splits a command line the way Windows does for programs.</summary>
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
