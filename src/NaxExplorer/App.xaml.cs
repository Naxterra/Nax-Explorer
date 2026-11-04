using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NaxExplorer.Core;
using NaxExplorer.Services;

namespace NaxExplorer;

public partial class App : Application
{
    private readonly List<MainWindow> _windows = [];
    private DispatcherQueue? _dispatcher;

    internal static App? Instance { get; private set; }

    internal AppSettings Settings { get; }

    internal SearchService Search { get; }

    public App()
    {
        Instance = this;
        Settings = AppSettings.Load();
        Loc.Apply(Settings.Language);
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception);
            throw;
        }
        // The theme is set for the whole app at start. Shared styles resolve their theme colours once, from the app's
        // theme, so a window that only overrides its own theme would mix dark text colours into a light window.
        if (Settings.Theme == "light") RequestedTheme = ApplicationTheme.Light;
        else if (Settings.Theme == "dark") RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            // Most failures come from one action, such as a broken thumbnail handler; the app keeps running.
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };
        Search = new SearchService();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            OpenWindow(Program.StartPath);
            Search.Start(Settings);
            Shell.ExplorerIntegration.Repair(Settings);
        }
        catch (Exception exception)
        {
            // Without a window the process would stay behind invisibly, so the user is told and the app ends.
            CrashLog.Write(exception);
            Shell.Native.MessageBox(IntPtr.Zero, Loc.F("StartFailed", DataFolders.CrashLog), "Nax Explorer", 0x10);
            Environment.Exit(1);
        }
    }

    internal MainWindow OpenWindow(string? path)
    {
        var window = new MainWindow(path);
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_windows.Count == 0) Search.Dispose();
        };
        window.Activate();
        return window;
    }

    /// <summary>Another start of the app: its folder opens in a new tab of the last window, or a new window opens.</summary>
    internal void OnRedirected(AppActivationArguments activation)
    {
        string? path = null;
        if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            path = StartArguments.LocationFrom(StartArguments.Split(launch.Arguments ?? ""));
        }
        _dispatcher?.SafeEnqueue(() =>
        {
            MainWindow? last = _windows.LastOrDefault();
            if (path is not null && last is not null) last.OpenInNewTab(path);
            else last = OpenWindow(path);
            last.BringToFront();
        });
    }

    internal IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>A key from the UI thread's keyboard hook, handed to the window that is in the foreground.</summary>
    internal static bool OnHookedKey(int virtualKey)
    {
        if (Instance is null) return false;
        IntPtr foreground = Shell.KeyboardHook.GetForegroundWindow();
        MainWindow? window = Instance._windows.FirstOrDefault(w => w.Hwnd == foreground);
        return window?.HandleHookedKey(virtualKey) ?? false;
    }

    /// <summary>Saves the settings; a failure is logged and never stops the app.</summary>
    internal void SaveSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex);
        }
    }
}

/// <summary>Appends unexpected exceptions to crash.log in the app's data folder.</summary>
internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DataFolders.Root);
            File.AppendAllText(DataFolders.CrashLog, $"{DateTime.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
