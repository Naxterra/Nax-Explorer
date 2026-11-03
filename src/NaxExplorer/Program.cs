using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NaxExplorer.Shell;

namespace NaxExplorer;

public static class Program
{
    /// <summary>The app's identity on the taskbar. The installer's shortcuts carry the same value.</summary>
    internal const string AppUserModelId = "Naxterra.NaxExplorer";

    /// <summary>The folder or file named on the command line, if any.</summary>
    internal static string? StartPath { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartPath = StartArguments.PathFrom(args);
        // One process serves every window: a second start hands its folder to the running app and ends.
        AppInstance key = AppInstance.FindOrRegisterForKey("Naxterra.NaxExplorer.Main");
        if (!key.IsCurrent)
        {
            Redirect(key);
            return 0;
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
    /// <summary>The first argument that names an existing folder or file, or null.</summary>
    public static string? PathFrom(IEnumerable<string> args)
    {
        foreach (string raw in args)
        {
            string arg = raw.Trim().Trim('"');
            if (arg.Length == 0 || arg.StartsWith('-') || arg.StartsWith('/')) continue;
            if (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(arg) && string.Equals(Path.GetFileName(arg), "Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? full = Core.PathTools.Normalize(arg, Environment.CurrentDirectory);
            if (full is not null && (Directory.Exists(full) || File.Exists(full))) return full;
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
