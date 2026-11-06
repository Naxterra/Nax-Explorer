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
        // Closing the last XAML window must not terminate an active copy or the folder watcher in the background.
        // RequestQuit decides when it is safe to exit, including when Explorer replacement is turned off.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
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
            // Started at sign-in only to catch Explorer windows: no window until one is needed.
            if (!Program.Background) OpenWindow(Program.StartPath);
            Search.Start(Settings);
            Shell.ExplorerIntegration.Repair(Settings);
            // Without the setting a start at sign-in has nothing to do, and the app ends again at once.
            UpdateBackground();
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
            // While the app stands in for Explorer it stays in the background to catch Explorer windows; otherwise
            // the last window ends it, as before.
            if (_windows.Count == 0 && !Settings.ReplaceExplorer) RequestQuit();
        };
        window.Activate();
        return window;
    }

    private Shell.ExplorerWindowCatcher? _catcher;
    private Shell.BackgroundWindow? _background;
    private bool _quitting;

    /// <summary>
    /// Follows the "open folders in Nax Explorer" setting: while it is on, the app starts at sign-in, keeps running
    /// without a window and catches Explorer windows other programs open; when it is off, all of that stops.
    /// </summary>
    internal void UpdateBackground()
    {
        bool on = Settings.ReplaceExplorer;
        // The app decides itself when to end, so a copy that still runs is never cut off by the last window closing.
        Shell.StartAtSignIn.Apply(on);
        if (on)
        {
            if (_dispatcher is null) return;
            _background ??= new Shell.BackgroundWindow(() => RequestQuit(forced: true));
            if (_catcher is null)
            {
                _catcher = new Shell.ExplorerWindowCatcher(_dispatcher, OpenCaught);
                _catcher.Start();
            }
            return;
        }
        _catcher?.Dispose();
        _catcher = null;
        _background?.Dispose();
        _background = null;
        if (_windows.Count == 0) RequestQuit();
    }

    /// <summary>Opens the places of an Explorer window that was caught, in the last window or a new one.</summary>
    private void OpenCaught(IReadOnlyList<Shell.CaughtPlace> places)
    {
        MainWindow? window = _windows.LastOrDefault();
        foreach (Shell.CaughtPlace place in places)
        {
            if (window is null) window = OpenWindow(place.Location);
            else if (place.Location is not null) window.OpenInNewTab(place.Location);
            if (place.Selection.Count > 0) window.SelectInActivePane(place.Selection);
        }
        window?.BringToFront();
    }

    private int _operations;
    private bool _quitWhenIdle;
    private bool _quitForced;

    /// <summary>A copy, move or delete started; the app does not end while one runs.</summary>
    internal void OperationStarted() => _operations++;

    /// <summary>A copy, move or delete ended; an end asked for meanwhile happens now, if it still applies.</summary>
    internal void OperationEnded()
    {
        if (--_operations > 0 || !_quitWhenIdle) return;
        _quitWhenIdle = false;
        // Setup's request always stands; an end because the last window closed only if no window was opened since.
        if (_quitForced || _windows.Count == 0 && !Settings.ReplaceExplorer) Quit();
    }

    /// <summary>
    /// Ends the app, but never in the middle of a copy, move or delete, whose file would be left half-written: then the
    /// end waits until the last one is done. Setup meanwhile keeps waiting for the app, which still holds its mutex.
    /// <paramref name="forced"/> is set when setup or Windows asks the app to end, rather than the last window closing.
    /// </summary>
    internal void RequestQuit(bool forced = false)
    {
        if (_operations > 0)
        {
            _quitWhenIdle = true;
            _quitForced |= forced;
            return;
        }
        Quit();
    }

    /// <summary>Ends the app: every window closes, so each saves its state, and then the process ends.</summary>
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _catcher?.Dispose();
        _catcher = null;
        _background?.Dispose();
        _background = null;
        foreach (MainWindow window in _windows.ToList()) window.Close();
        Search.Dispose();
        Exit();
    }

    /// <summary>Another start of the app: its folder opens in a new tab of the last window, or a new window opens.</summary>
    internal void OnRedirected(AppActivationArguments activation)
    {
        string? path = null;
        if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            List<string> args = StartArguments.Split(launch.Arguments ?? "");
            // A start at sign-in while the app already runs in the background has nothing to add.
            if (args.Any(StartArguments.IsBackgroundSwitch)) return;
            path = StartArguments.LocationFrom(args);
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
