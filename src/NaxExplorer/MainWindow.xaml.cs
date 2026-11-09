using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Controls;
using NaxExplorer.Core;
using NaxExplorer.Search;
using NaxExplorer.Services;
using NaxExplorer.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Graphics;
using Windows.System;

namespace NaxExplorer;

/// <summary>An entry of the sidebar.</summary>
public sealed class SidebarEntry
{
    public required string Glyph { get; init; }

    public required string Label { get; init; }

    public required string Location { get; init; }

    public Brush? Brush { get; init; }

    public double Used { get; init; }

    public Brush? UsageBrush { get; init; }

    public string Detail { get; init; } = "";

    public Visibility UsageVisibility => UsageBrush is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Entries for places Windows lists itself, such as the Recycle Bin or Network. They open in the app like folders,
    /// but nothing can be copied into them, and they are no target in the folder chooser.
    /// </summary>
    public bool IsShellPlace { get; init; }

    /// <summary>For quick access entries: the entry as the settings store it, a key such as "::Downloads" or a path.</summary>
    public string? QuickKey { get; init; }
}

public sealed partial class MainWindow : Window
{
    private readonly Native.SubclassProc _subclass;
    private readonly DispatcherQueueTimerWrapper _messageTimer;
    private readonly DispatcherQueueTimerWrapper _previewTimer;
    private FilePane _active;
    private HashSet<string> _cut = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RestorePlacement();
        // Shell submenus such as "Send to" are filled only when the window passes menu messages to the shell menu.
        _subclass = (hwnd, message, wParam, lParam, _, _) =>
            ShellContextMenu.TryHandleMessage(message, wParam, lParam, out IntPtr result) ? result : Native.DefSubclassProc(hwnd, message, wParam, lParam);
        Native.SetWindowSubclass(Hwnd, _subclass, 1, 0);

        _messageTimer = new DispatcherQueueTimerWrapper(DispatcherQueue, TimeSpan.FromSeconds(5), () => Message.IsOpen = false);
        _previewTimer = new DispatcherQueueTimerWrapper(DispatcherQueue, TimeSpan.FromMilliseconds(120), UpdatePreview);
        Root.Loaded += (_, _) => ObserveRasterizationScale();
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) =>
        {
            LeftPane.ApplyDisplaySettings(reload: false);
            RightPane.ApplyDisplaySettings(reload: false);
            BuildSidebar();
        };

        AppSettings settings = Settings;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        LeftPane.Initialize(this, StartTabs(settings.LeftStart, settings.Left, home), startPath, home);
        RightPane.Initialize(this, StartTabs(settings.RightStart, settings.Right, home), null, PathTools.ThisPc);
        _active = LeftPane;
        SetActivePane(LeftPane);
        UpdateTitle();
        SidebarColumn.Width = new GridLength(Math.Clamp(settings.SidebarWidth, 160, 480));
        PreviewColumn.Width = new GridLength(Math.Clamp(settings.PreviewWidth, 220, 900));
        ApplyLayout();
        BuildSidebar();
        UpdateAiCard();
        Preview.Attach(this);
        Viewer.Attach(this);

        Action<IndexerStatus> statusChanged = _ => DispatcherQueue.SafeEnqueue(UpdateAiCard);
        Action availabilityChanged = () => DispatcherQueue.SafeEnqueue(UpdateAiCard);
        App.Instance!.Search.StatusChanged += statusChanged;
        App.Instance.Search.AvailabilityChanged += availabilityChanged;
        Closed += (_, _) =>
        {
            SaveState();
            App.Instance.Search.StatusChanged -= statusChanged;
            App.Instance.Search.AvailabilityChanged -= availabilityChanged;
            _messageTimer.Stop();
            _previewTimer.Stop();
            if (_observedRoot is not null) _observedRoot.Changed -= XamlRoot_Changed;
            _observedRoot = null;
            Preview.StopBackgroundWork();
            LeftPane.StopBackgroundWork();
            RightPane.StopBackgroundWork();
        };
        // Listed with Windows' open folder windows, so "Show in folder" in other programs finds this window and the
        // file it names gets selected here.
        _shellWindow = new ShellWindowRegistration(Hwnd, (name, only) => DispatcherQueue.SafeEnqueue(() =>
        {
            _active.SelectFromShell(name, only);
            BringToFront();
        }));
        Closed += (_, _) => _shellWindow.Dispose();
        bool firstActivation = true;
        Activated += (_, args) =>
        {
            _shellWindow.Activated(args.WindowActivationState != WindowActivationState.Deactivated);
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            _active.UpdateCutMarks();
            // On its first activation the window gives the focus to its first control; the list takes it back, so
            // typing works right away.
            if (firstActivation) _active.FocusListSoon();
            firstActivation = false;
        };
        LeftPane.FocusAfterFirstLoad = true;
        KeyboardHook.Install(App.OnHookedKey);
    }

    /// <summary>F7 from the keyboard hook: copy to the other pane, unless a text box or the viewer has the focus.</summary>
    internal bool HandleHookedKey(int virtualKey)
    {
        if (virtualKey != 0x76 || Viewer.Visibility == Visibility.Visible || Root.XamlRoot is null) return false;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox) return false;
        if (Native.IsKeyDown(0x11) || Native.IsKeyDown(0x12)) return false;
        DispatcherQueue.SafeEnqueue(() => _active.TransferToOtherPane(move: false));
        return true;
    }

    internal IntPtr Hwnd { get; }

    private XamlRoot? _observedRoot;
    private double _rasterizationScale = 1;

    private void ObserveRasterizationScale()
    {
        if (_observedRoot == Root.XamlRoot) return;
        if (_observedRoot is not null) _observedRoot.Changed -= XamlRoot_Changed;
        _observedRoot = Root.XamlRoot;
        if (_observedRoot is null) return;
        _rasterizationScale = _observedRoot.RasterizationScale;
        _observedRoot.Changed += XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = sender.RasterizationScale;
        if (scale == _rasterizationScale) return;
        _rasterizationScale = scale;
        DispatcherQueue.SafeEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_observedRoot is null || scale != _observedRoot.RasterizationScale) return;
            LeftPane.RefreshImagesForDpi();
            RightPane.RefreshImagesForDpi();
            _previewTimer.Restart();
            Viewer.OnRasterizationScaleChanged();
        });
    }

    internal AppSettings Settings => App.Instance!.Settings;

    internal bool IsDark => Root.ActualTheme == ElementTheme.Dark;

    internal FilePane ActivePane => _active;

    internal HashSet<string> CutPaths => _cut;

    // ---------------------------------------------------------------- window

    private void RestorePlacement()
    {
        if (Settings.Window is { Width: > 400, Height: > 300 } placement && App.Instance!.Windows.Count == 0)
        {
            var area = DisplayArea.GetFromRect(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height), DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;
            int width = Math.Min(placement.Width, work.Width), height = Math.Min(placement.Height, work.Height);
            int x = Math.Clamp(placement.X, work.X, work.X + work.Width - width), y = Math.Clamp(placement.Y, work.Y, work.Y + work.Height - height);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        else
        {
            AppWindow.Resize(new SizeInt32(1600, 960));
        }
    }

    private void SaveState()
    {
        AppSettings settings = Settings;
        settings.Left = LeftPane.Save();
        settings.Right = RightPane.Save();
        settings.SidebarWidth = SidebarColumn.ActualWidth;
        if (settings.ShowPreview) settings.PreviewWidth = PreviewColumn.ActualWidth;
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) maximized = settings.Window?.Maximized ?? false;
        else settings.Window = new WindowPlacement { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height, Maximized = maximized };
        App.Instance!.SaveSettings();
    }

    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        Native.SetForegroundWindow(Hwnd);
    }

    internal void OpenInNewTab(string path)
    {
        _active.OpenTab(path);
        _active.FocusList();
    }

    /// <summary>Selects items in the active pane, as soon as its folder is shown.</summary>
    internal void SelectInActivePane(IReadOnlyList<string> paths)
    {
        for (int i = 0; i < paths.Count; i++) _active.SelectFromShell(paths[i], only: i == 0);
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = Settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = Settings.Theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void ApplyLayout()
    {
        bool dual = Settings.DualPane;
        RightPane.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PaneSplitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        PaneGapColumn.Width = new GridLength(dual ? 6 : 0);
        RightColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!dual) LeftColumn.Width = new GridLength(1, GridUnitType.Star);
        if (!dual && _active == RightPane) SetActivePane(LeftPane);
        DualToggle.IsChecked = dual;
        // With one pane, copy and move ask for the folder instead of using the other pane, so they stay available.
        CopyOtherButton.IsEnabled = MoveOtherButton.IsEnabled = true;
        bool preview = Settings.ShowPreview;
        Preview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewGapColumn.Width = new GridLength(preview ? 6 : 0);
        PreviewColumn.Width = preview ? new GridLength(Math.Clamp(Settings.PreviewWidth, 220, 900)) : new GridLength(0);
        PreviewToggle.IsChecked = preview;
        if (preview) UpdatePreview();
    }

    // ---------------------------------------------------------------- panes

    internal void SetActivePane(FilePane pane)
    {
        if (pane.Visibility != Visibility.Visible) return;
        bool changed = _active != pane;
        _active = pane;
        LeftPane.SetActive(pane == LeftPane && Settings.DualPane);
        RightPane.SetActive(pane == RightPane);
        if (changed)
        {
            UpdateTitle();
            if (pane.IsSelectingWithMouse) OnPaneMouseSelectionStarted(pane);
            else
            {
                Preview.PrepareSelection(pane.SelectedItems, pane.PreviewLocation);
                _previewTimer.Restart();
            }
            _shellWindow?.Navigated(pane.Location);
        }
    }

    /// <summary>The tabs a pane opens with: the last session's, or one tab at the start folder chosen in the settings.</summary>
    private static PaneSettings StartTabs(string startSetting, PaneSettings saved, string home)
    {
        string? start = AppSettings.StartPlace(startSetting, home);
        if (start is null) return saved;
        return new PaneSettings { Tabs = [start], ActiveTab = 0, View = saved.View, Sort = saved.Sort, Descending = saved.Descending };
    }

    /// <summary>
    /// The last tab of a pane was closed. In dual-pane mode that pane closes: the right one simply disappears, and when
    /// the left one is closed it takes over the right pane's tabs. With a single pane the window closes, as in Explorer.
    /// </summary>
    internal void CloseLastTab(FilePane pane)
    {
        if (!Settings.DualPane)
        {
            Close();
            return;
        }
        if (pane == LeftPane)
        {
            var (locations, active) = RightPane.TabLocations();
            LeftPane.ResetTabs(locations, active);
        }
        Settings.DualPane = false;
        ApplyLayout();
        SetActivePane(LeftPane);
        LeftPane.FocusListSoon();
        App.Instance!.SaveSettings();
    }

    internal FilePane? OtherPane(FilePane pane) => !Settings.DualPane ? null : pane == LeftPane ? RightPane : LeftPane;

    internal async void OpenInOtherPane(FilePane pane, string location)
    {
        if (!Settings.DualPane)
        {
            Settings.DualPane = true;
            ApplyLayout();
        }
        FilePane other = OtherPane(pane)!;
        await other.NavigateAsync(location);
    }

    private readonly ShellWindowRegistration _shellWindow;

    internal void OnPaneLocationChanged(FilePane pane)
    {
        if (pane != _active) return;
        Preview.PrepareSelection(pane.SelectedItems, pane.PreviewLocation);
        UpdateTitle();
        _shellWindow?.Navigated(pane.Location);
        // Another tab or folder in the active pane: the details panel shows that folder now. The address bar reports
        // the same folder again when its names arrive. Shell names can arrive after the first preview, so refresh
        // those too rather than leaving a parsing GUID in the heading.
        if (PathTools.IsShellPlace(pane.Location) || !string.Equals(pane.PreviewLocation, _previewFolder, StringComparison.OrdinalIgnoreCase)) _previewTimer.Restart();
    }

    /// <summary>The folder the details panel was last filled for.</summary>
    private string? _previewFolder;

    internal void OnPaneSelectionChanged(FilePane pane)
    {
        if (pane != _active || pane.IsSelectingWithMouse) return;
        // Retire the old read now, before the debounce timer can fire. Its text or
        // bitmap must not land during the user's next click and block that paint.
        Preview.PrepareSelection(pane.SelectedItems, pane.PreviewLocation);
        _previewTimer.Restart();
    }

    internal void OnPaneMouseSelectionStarted(FilePane pane)
    {
        if (pane != _active) return;
        _previewTimer.Stop();
        Preview.StopBackgroundWork();
    }

    private void UpdateTitle()
    {
        string location = _active.Location;
        string name = _active.IsSearchOpen ? Loc.T("SearchResults") : FilePane.PlaceName(location);
        Title = $"{name} - Nax Explorer";
        // Written with the names Windows shows, as C:\Benutzer\Anna; the names come from the cache the address bar fills.
        TitleLocation.Text = PathTools.IsThisPc(location) ? Loc.T("ThisPC") : ShellNames.KnownPath(location);
    }

    /// <summary>Refreshes every pane that shows a folder, for example after files were copied into it.</summary>
    internal void RefreshPanesShowing(string folder)
    {
        foreach (MainWindow window in App.Instance!.Windows)
        {
            window.InvalidatePreviewTotals(folder);
            foreach (FilePane pane in new[] { window.LeftPane, window.RightPane })
            {
                if (pane.Folder is { } shown && PathTools.SameLocation(shown, folder)) pane.ScheduleRefresh();
            }
        }
    }

    internal void OnPaneContentsChanged(string folder, FolderChanges changes)
    {
        // Recursive totals can describe an ancestor of the changed folder, including in another window.
        foreach (MainWindow window in App.Instance!.Windows) window.InvalidatePreviewTotals(folder, changes);
    }

    internal void OnPaneReloaded(FilePane pane)
    {
        // Opening or reloading another pane is not a change to this window's selected file.
        if (pane != _active) return;
        Preview.Reload();
        _previewTimer.Restart();
    }

    private void InvalidatePreviewTotals(string folder, FolderChanges? changes = null)
    {
        if (Preview.InvalidateTotals(folder, changes)) _previewTimer.Restart();
    }

    internal void RefreshPreviewSoon() => _previewTimer.Restart();

    internal void SetCutPaths(IReadOnlyList<string> paths)
    {
        _cut = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        LeftPane.UpdateCutMarks();
        RightPane.UpdateCutMarks();
    }

    private void UpdatePreview()
    {
        if (!Settings.ShowPreview || _active.IsSelectingWithMouse || IsViewerOpen) return;
        _previewFolder = _active.PreviewLocation;
        Preview.Show(_active.SelectedItems, _previewFolder);
    }

    internal void ShowViewer(IReadOnlyList<string> pictures, int index, FilePane source)
    {
        _previewTimer.Stop();
        Preview.StopBackgroundWork();
        LeftPane.IsEnabled = RightPane.IsEnabled = false;
        Viewer.Open(pictures, index, source);
    }

    internal bool IsViewerOpen => Viewer.Visibility == Visibility.Visible;

    internal void OnViewerClosed()
    {
        LeftPane.IsEnabled = RightPane.IsEnabled = true;
        _previewTimer.Restart();
    }

    // ---------------------------------------------------------------- messages

    internal void ShowMessage(string text, InfoBarSeverity severity)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.IsOpen = true;
        _messageTimer.Restart();
    }

    // ---------------------------------------------------------------- sidebar

    private void BuildSidebar()
    {
        bool dark = IsDark;
        Brush Color(byte r, byte g, byte b) => new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var specials = new Dictionary<string, SidebarEntry>
        {
            [QuickAccessList.Home] = new() { QuickKey = QuickAccessList.Home, Glyph = "", Label = Loc.T("Home"), Location = profile, Brush = Color(0x3B, 0x82, 0xF6) },
            [QuickAccessList.Desktop] = new() { QuickKey = QuickAccessList.Desktop, Glyph = "", Label = Loc.T("Desktop"), Location = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Brush = Color(0x06, 0xB6, 0xD4) },
            [QuickAccessList.Downloads] = new() { QuickKey = QuickAccessList.Downloads, Glyph = "", Label = Loc.T("Downloads"), Location = Path.Combine(profile, "Downloads"), Brush = Color(0x10, 0xB9, 0x81) },
            [QuickAccessList.Documents] = new() { QuickKey = QuickAccessList.Documents, Glyph = "", Label = Loc.T("Documents"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Brush = Color(0x63, 0x66, 0xF1) },
            [QuickAccessList.Pictures] = new() { QuickKey = QuickAccessList.Pictures, Glyph = "", Label = Loc.T("Pictures"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Brush = Color(0xEC, 0x48, 0x99) },
            [QuickAccessList.Music] = new() { QuickKey = QuickAccessList.Music, Glyph = "", Label = Loc.T("Music"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Brush = Color(0xF5, 0x9E, 0x0B) },
            [QuickAccessList.Videos] = new() { QuickKey = QuickAccessList.Videos, Glyph = "", Label = Loc.T("Videos"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Brush = Color(0x8B, 0x5C, 0xF6) },
            [QuickAccessList.RecycleBin] = new() { QuickKey = QuickAccessList.RecycleBin, Glyph = "", Label = Loc.T("RecycleBin"), Location = PathTools.ShellPlace(ShellFolderReader.RecycleBin), Brush = Color(0x94, 0xA3, 0xB8), IsShellPlace = true },
        };
        Brush folderBrush = new SolidColorBrush(Palette.Of(FileCategory.Folder, dark));
        var quick = new List<SidebarEntry>();
        foreach (string key in QuickAccessList.Current(Settings))
        {
            if (specials.TryGetValue(key, out SidebarEntry? special))
            {
                if (special.IsShellPlace || Directory.Exists(special.Location)) quick.Add(special);
                continue;
            }
            // The stored name shows at once; Windows is asked in the background, because a folder on a share that does
            // not answer can take seconds and must not freeze the window.
            quick.Add(new SidebarEntry { Glyph = "", Label = ShellNames.Known(key), Location = key, Brush = folderBrush, QuickKey = key });
        }
        QuickList.ItemsSource = quick;
        if (quick.Any(e => !QuickAccessList.IsKey(e.QuickKey!))) _ = NameQuickFoldersAsync(quick);
        _ = LoadCloudAsync();
        _ = LoadDrivesAsync();
    }

    private async Task NameQuickFoldersAsync(List<SidebarEntry> quick)
    {
        string[] names = await Task.Run(() => quick.Select(e => QuickAccessList.IsKey(e.QuickKey!) ? e.Label : ShellNames.OfFolder(e.Location)).ToArray());
        if (!ReferenceEquals(QuickList.ItemsSource, quick) || quick.Select(e => e.Label).SequenceEqual(names)) return;
        QuickList.ItemsSource = quick.Select((e, i) => e.QuickKey is { } key && QuickAccessList.IsKey(key) ? e
            : new SidebarEntry { Glyph = e.Glyph, Label = names[i], Location = e.Location, Brush = e.Brush, QuickKey = e.QuickKey }).ToList();
    }

    /// <summary>
    /// Shows the folders of cloud sync apps, such as Nextcloud, OneDrive or Proton Drive, in their own section. Drives
    /// are asked in the background to find Google Drive; the result is shared with the lists for the status marks.
    /// </summary>
    private async Task LoadCloudAsync()
    {
        ShowCloud(CloudRoots.Folders);
        List<CloudFolder> found = await Task.Run(() => CloudFolders.Find(withDrives: true));
        CloudRoots.Set(found);
        ShowCloud(found);
    }

    private void ShowCloud(IReadOnlyList<CloudFolder> folders)
    {
        CloudList.ItemsSource = folders.Select(f => new SidebarEntry
        {
            Glyph = "",
            Label = f.Name,
            Location = f.Path,
            Brush = new SolidColorBrush(ProviderColor(f.Provider)),
        }).ToList();
        CloudHeader.Visibility = CloudList.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The colour each cloud app is known by, so its entry is found at a glance.</summary>
    private static Windows.UI.Color ProviderColor(string provider)
    {
        string id = provider.ToLowerInvariant();
        (byte r, byte g, byte b) = id switch
        {
            _ when id.StartsWith("nextcloud") => ((byte)0x00, (byte)0x82, (byte)0xC9),
            _ when id.StartsWith("onedrive") => ((byte)0x00, (byte)0x78, (byte)0xD4),
            _ when id.StartsWith("dropbox") => ((byte)0x00, (byte)0x61, (byte)0xFF),
            _ when id.StartsWith("proton") => ((byte)0x6D, (byte)0x4A, (byte)0xFF),
            _ when id.StartsWith("google") || id.StartsWith("drivefs") => ((byte)0x34, (byte)0xA8, (byte)0x53),
            _ when id.StartsWith("icloud") => ((byte)0x36, (byte)0x93, (byte)0xF3),
            _ => ((byte)0x38, (byte)0xBD, (byte)0xF8),
        };
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private async Task LoadDrivesAsync()
    {
        List<FileEntry> drives = await Task.Run(DirectoryReader.ReadDrives);
        DriveList.ItemsSource = drives.Select(d =>
        {
            double used = d.Size > 0 ? 1 - (double)d.FreeBytes / d.Size : 0;
            string label = d.Label.Length > 0 ? d.Label : Loc.Drive(d.DriveType);
            return new SidebarEntry
            {
                Glyph = d.DriveType switch { DriveType.Network => "", DriveType.Removable => "", DriveType.CDRom => "", _ => "" },
                Label = $"{label} ({d.Name})",
                Location = d.FullPath,
                Brush = new SolidColorBrush(Palette.Of(FileCategory.DiskImage, IsDark)),
                Used = used,
                UsageBrush = d.Size > 0 ? new SolidColorBrush(Palette.Usage(used)) : null,
                Detail = d.Size > 0 ? Loc.F("DriveFree", SizeFormat.Format(d.FreeBytes), SizeFormat.Format(d.Size)) : "",
            };
        }).Append(new SidebarEntry
        {
            // The computers and shares of the local network, as under "Netzwerk" in Explorer.
            Glyph = "",
            Label = Loc.T("Network"),
            Location = PathTools.ShellPlace(NetworkPlace),
            Brush = new SolidColorBrush(Palette.Of(FileCategory.DiskImage, IsDark)),
            IsShellPlace = true,
        }).ToList();
    }

    /// <summary>The parsing name of Network, the computers and shares of the local network.</summary>
    private const string NetworkPlace = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

    private async void Sidebar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarEntry entry) return;
        await _active.NavigateAsync(entry.Location);
    }

    /// <summary>True for the Recycle Bin's own entry, onto which dropped items are deleted, as in Explorer.</summary>
    private static bool IsRecycleBin(SidebarEntry entry) =>
        PathTools.SameLocation(entry.Location, PathTools.ShellPlace(ShellFolderReader.RecycleBin));

    /// <summary>A middle click opens the entry in a new tab.</summary>
    private void Sidebar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarEntry entry)
        {
            _active.OpenTab(entry.Location);
            e.Handled = true;
        }
    }

    /// <summary>The quick access menu: open in a new tab, move up or down, remove, or bring back the list as it came.</summary>
    private void QuickList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarEntry { QuickKey: { } key } entry) return;
        var menu = new MenuFlyout();
        MenuFlyoutItem Command(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.T(text), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Command("OpenInNewTab", "", () => _active.OpenTab(entry.Location)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Command("MoveUp", "", () => ChangeQuickAccess(settings => QuickAccessList.Move(settings, key, -1))));
        menu.Items.Add(Command("MoveDown", "", () => ChangeQuickAccess(settings => QuickAccessList.Move(settings, key, 1))));
        menu.Items.Add(Command("Unpin", "", () => ChangeQuickAccess(settings => QuickAccessList.Remove(settings, key))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Command("QuickAccessReset", "", () => ChangeQuickAccess(QuickAccessList.Reset)));
        menu.ShowAt((FrameworkElement)e.OriginalSource);
        e.Handled = true;
    }

    internal void Pin(string folder) => ChangeQuickAccess(settings => QuickAccessList.Add(settings, folder));

    /// <summary>Changes the quick access list, saves it and shows it in every window.</summary>
    private static void ChangeQuickAccess(Action<AppSettings> change)
    {
        change(App.Instance!.Settings);
        App.Instance.SaveSettings();
        foreach (MainWindow window in App.Instance.Windows) window.BuildSidebar();
    }

    /// <summary>The sidebar entry under the pointer, found by hit-testing, because the event's source is not reliably the row.</summary>
    private static SidebarEntry? SidebarEntryAt(object sender, DragEventArgs e)
    {
        if (sender is UIElement list)
        {
            foreach (UIElement element in VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), list))
            {
                if (element is SelectorItem { Content: SidebarEntry entry }) return entry;
            }
        }
        return (e.OriginalSource as FrameworkElement)?.DataContext as SidebarEntry;
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Properties.ContainsKey("NaxExplorer.Paths");
        if (files && SidebarEntryAt(sender, e) is { } bin && IsRecycleBin(bin))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = Loc.T("DropToRecycleBin");
            return;
        }
        if (SidebarEntryAt(sender, e) is { IsShellPlace: false } entry && files)
        {
            string[]? sources = e.DataView.Properties.TryGetValue("NaxExplorer.Paths", out object? ownValue) ? ownValue as string[] : null;
            bool move = DropPolicy.Moves(e.Modifiers.HasFlag(DragDropModifiers.Control) || Native.IsKeyDown(0x11),
                e.Modifiers.HasFlag(DragDropModifiers.Shift) || Native.IsKeyDown(0x10), sources, entry.Location);
            e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.F(move ? "DropMove" : "DropCopy", entry.Label);
            return;
        }
        e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void Sidebar_Drop(object sender, DragEventArgs e)
    {
        if (SidebarEntryAt(sender, e) is not { } entry || entry.IsShellPlace && !IsRecycleBin(entry)) return;
        DragOperationDeferral deferral = e.GetDeferral();
        bool control = e.Modifiers.HasFlag(DragDropModifiers.Control) || Native.IsKeyDown(0x11);
        bool shift = e.Modifiers.HasFlag(DragDropModifiers.Shift) || Native.IsKeyDown(0x10);
        try
        {
            string[]? own = e.DataView.Properties.TryGetValue("NaxExplorer.Paths", out object? value) ? value as string[] : null;
            List<string> paths = own is not null ? [.. own] : [];
            if (paths.Count == 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            if (IsRecycleBin(entry))
            {
                // Dropping on the Recycle Bin deletes, as in Explorer; items of shell places are left alone.
                List<string> files = paths.Where(p => !PathTools.IsShellPlace(p)).ToList();
                if (files.Count > 0) await DeleteAsync(files, permanently: false);
                return;
            }
            // Decided from the same input as the pointer during the drag: the app's own paths. A drag from another
            // program has none there, so it copies unless Shift was held, exactly as the pointer said.
            bool move = DropPolicy.Moves(control, shift, own, entry.Location);
            if (paths.Count == 0 || TransferCheck.Check(paths, entry.Location, move) != TransferProblem.None) return;
            await TransferAsync(paths, entry.Location, move);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------------------------------------------------------- AI card

    private void UpdateAiCard()
    {
        SearchService search = App.Instance!.Search;
        AiProgress.Visibility = Visibility.Collapsed;
        AiPauseButton.Visibility = Visibility.Collapsed;
        if (!Settings.AiSearch)
        {
            AiStatusText.Text = Loc.T("AiOffHint");
            return;
        }
        if (!EmbeddingModel.IsInstalled())
        {
            AiStatusText.Text = Loc.T("AiNeedsModel");
            return;
        }
        if (search.Status is not { } status)
        {
            AiStatusText.Text = search.LastError is { } error ? Loc.F("AiFailed", error) : Loc.T("AiStarting");
            return;
        }
        AiPauseButton.Visibility = Visibility.Visible;
        AiPauseGlyph.Glyph = search.UserPaused ? "" : "";
        ToolTipService.SetToolTip(AiPauseButton, Loc.T(search.UserPaused ? "ResumeIndexing" : "PauseIndexing"));
        AutomationProperties.SetName(AiPauseButton, Loc.T(search.UserPaused ? "ResumeIndexing" : "PauseIndexing"));
        string documents = Loc.F("AiDocuments", status.Documents.ToString("N0"), status.Files.ToString("N0"));
        string device = search.Device is { } d ? "  ·  " + d : "";
        switch (status.Phase)
        {
            case IndexerPhase.Scanning:
                AiStatusText.Text = Loc.T("AiScanning") + "\n" + documents;
                AiProgress.Visibility = Visibility.Visible;
                break;
            case IndexerPhase.Indexing:
                AiStatusText.Text = Loc.F("AiIndexing", status.Queued.ToString("N0")) + device + "\n" + documents;
                AiProgress.Visibility = Visibility.Visible;
                break;
            case IndexerPhase.Paused:
                AiStatusText.Text = Loc.T("AiPaused" + (status.Detail ?? "user")) + "\n" + documents;
                break;
            case IndexerPhase.Failed:
                AiStatusText.Text = Loc.F("AiFailed", status.Detail ?? "");
                break;
            default:
                AiStatusText.Text = Loc.T("AiReady") + device + "\n" + documents;
                break;
        }
    }

    private void AiCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside(source, AiPauseButton)) return;
        OpenSettings(aiSection: true);
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == container) return true;
        }
        return false;
    }

    private void AiPause_Click(object sender, RoutedEventArgs e)
    {
        SearchService search = App.Instance!.Search;
        search.SetUserPaused(!search.UserPaused);
        UpdateAiCard();
    }

    internal void AddToIndex(string folder)
    {
        if (!Settings.AiSearch)
        {
            OpenSettings(aiSection: true);
            return;
        }
        if (Settings.IndexedFolders.Any(f => PathTools.IsSameOrInside(folder, f))) return;
        // A parent folder replaces the folders inside it.
        Settings.IndexedFolders.RemoveAll(f => PathTools.IsSameOrInside(f, folder));
        Settings.IndexedFolders.Add(folder);
        App.Instance!.SaveSettings();
        App.Instance.Search.Apply(Settings, restart: false);
        ShowMessage(Loc.F("AddedToIndex", folder), InfoBarSeverity.Success);
    }

    /// <summary>
    /// Copies or moves with the app's own windows: it asks about conflicts first, then runs the copy engine silently
    /// with a progress card in the corner. Returns false when the user cancelled before anything started.
    /// </summary>
    internal async Task<bool> TransferAsync(IReadOnlyList<string> paths, string target, bool move)
    {
        TransferConflicts conflicts = await Task.Run(() => TransferPlan.FindConflicts(paths, target));
        // Without a question there is no answer: a conflict that appears only later is left to Windows to ask about.
        ConflictChoice? choice = null;
        if (conflicts.Any)
        {
            if (await AskSafelyAsync(() => FileOperationDialogs.AskConflictAsync(Root.XamlRoot, Root.RequestedTheme, conflicts, FilePane.PlaceName(target), move)) is not { } picked) return false;
            choice = picked;
        }
        IReadOnlyList<TransferBatch> batches = await Task.Run(() => TransferPlan.Plan(paths, target, move, choice, conflicts.Items));
        if (batches.Count == 0) return true;
        string title = Loc.F(move ? "MovingTitle" : "CopyingTitle", paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Loc.F("ItemCount", paths.Count), FilePane.PlaceName(target));
        bool completed = await RunOperationAsync(title, progress => ShellActions.TransferAsync(batches, move, Hwnd, progress));
        // The target and the folders above it grew, and an item of the same name there may have been replaced. Moved
        // items are gone from where they were, with everything inside them. The target's other folders are unchanged.
        ForgetFolderSizes(new[] { target }, withContents: false);
        ForgetFolderSizes(paths.Select(p => Path.GetFileName(p.TrimEnd('\\'))).Where(n => n.Length > 0).Select(n => Path.Combine(target, n)), withContents: true);
        if (move) ForgetFolderSizes(paths, withContents: true);
        RefreshPanesShowing(target);
        if (move)
        {
            foreach (string folder in paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\'))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) RefreshPanesShowing(folder);
        }
        return completed;
    }

    /// <summary>
    /// Deletes to the Recycle Bin, or for good with <paramref name="permanently"/> after the app asked. Items on a drive
    /// without a Recycle Bin, such as a network share, are never deleted for good without that question either.
    /// Returns false when nothing was deleted because the user said no.
    /// </summary>
    internal async Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanently)
    {
        if (paths.Count == 0) return false;
        // Items of the Recycle Bin itself, or of another shell place, can only be deleted for good.
        if (paths.Any(PathTools.IsShellPlace)) permanently = true;
        bool noBin = !permanently && !await Task.Run(() => paths.All(FileOperationDialogs.HasRecycleBin));
        if (permanently || noBin)
        {
            if (await AskSafelyAsync(async () => (bool?)await FileOperationDialogs.ConfirmPermanentDeleteAsync(Root.XamlRoot, Root.RequestedTheme, paths, noBin)) != true) return false;
            permanently = true;
        }
        string what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Loc.F("ItemCount", paths.Count);
        await RunOperationAsync(Loc.F(permanently ? "DeletingForGoodTitle" : "DeletingTitle", what), progress => ShellActions.DeleteAsync(paths, Hwnd, permanently, progress));
        ForgetFolderSizes(paths, withContents: true);
        foreach (string folder in paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\'))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) RefreshPanesShowing(folder);
        return true;
    }

    /// <summary>
    /// Drops the kept folder sizes a copy, move or delete at these places made wrong: each place and the folders above
    /// it, and with <paramref name="withContents"/> also everything inside it.
    /// </summary>
    private static void ForgetFolderSizes(IEnumerable<string> places, bool withContents)
    {
        foreach (string place in places.Where(p => !PathTools.IsShellPlace(p)))
        {
            if (withContents) FolderSizeCache.Shared.ForgetWithContents(place);
            else FolderSizeCache.Shared.Forget(place);
        }
    }

    /// <summary>Runs one operation with its progress card; a failure ends up on the card instead of being thrown.</summary>
    private async Task<bool> RunOperationAsync(string title, Func<OperationProgress, Task> run)
    {
        var progress = new OperationProgress();
        var card = new OperationCard(OperationsPanel, title, progress);
        long lastUpdate = 0;
        progress.Changed += (share, item) =>
        {
            // The engine reports very often; the card is updated at most every tenth of a second.
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastUpdate) < 100) return;
            Interlocked.Exchange(ref lastUpdate, now);
            DispatcherQueue.SafeEnqueue(() => card.Update(share, item));
        };
        string? error = null;
        // The app does not end while this runs, so no file is left half-written by a closing app.
        App.Instance?.OperationStarted();
        try
        {
            await run(progress);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            error = ex.Message;
        }
        finally
        {
            App.Instance?.OperationEnded();
        }
        card.Finish(error);
        return error is null && !progress.IsCancelled && progress.Failures.Count == 0;
    }

    /// <summary>Shows one of the app's questions; when the window cannot show it, the answer is "cancel".</summary>
    private async Task<T?> AskSafelyAsync<T>(Func<Task<T?>> ask) where T : struct
    {
        try
        {
            return await ask();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            CrashLog.Write(ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    /// <summary>
    /// Asks in the app's own window where "Copy to" or "Move to" should go when only one pane is open. Returns the
    /// chosen folder, or null when the user cancelled or the window could not open.
    /// </summary>
    internal async Task<string?> ChooseFolderAsync(bool move, IReadOnlyList<string> paths, string? paneFolder)
    {
        IEnumerable<SidebarEntry> places = new[] { QuickList, CloudList, DriveList }
            .SelectMany(list => list.ItemsSource as IEnumerable<SidebarEntry> ?? []);
        try
        {
            var dialog = new FolderChooser(move, paths, paneFolder, places.ToList(), new ListingOptions(Settings.ShowHidden, Settings.ShowProtectedFiles), IsDark)
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.Chosen : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // Another dialog is already open, or the window is closing.
            CrashLog.Write(ex);
            ShowMessage(ex.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    // ---------------------------------------------------------------- settings

    private async void OpenSettings(bool aiSection)
    {
        var dialog = new SettingsDialog(Settings, aiSection) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
        await dialog.ShowAsync();
        bool languageChanged = dialog.LanguageChanged;
        bool themeChanged = dialog.ThemeChanged;
        App.Instance!.SaveSettings();
        ApplyTheme();
        ApplyLayout();
        foreach (MainWindow window in App.Instance.Windows)
        {
            window.LeftPane.ApplyDisplaySettings(reload: true);
            window.RightPane.ApplyDisplaySettings(reload: true);
            window.BuildSidebar();
            window.UpdateAiCard();
        }
        if (languageChanged || themeChanged) ShowMessage(Loc.T(languageChanged ? "LanguageAfterRestart" : "ThemeAfterRestart"), InfoBarSeverity.Informational);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(aiSection: false);

    // ---------------------------------------------------------------- commands

    private void NewFolder_Click(object sender, RoutedEventArgs e) => _active.NewFolder();

    private void NewTextFile_Click(object sender, RoutedEventArgs e) => _active.NewTextFile();

    private void NewWindow_Click(object sender, RoutedEventArgs e) => App.Instance!.OpenWindow(_active.Folder);

    private void Cut_Click(object sender, RoutedEventArgs e) => _active.Copy(cut: true);

    private void Copy_Click(object sender, RoutedEventArgs e) => _active.Copy(cut: false);

    private void Paste_Click(object sender, RoutedEventArgs e) => _active.Paste();

    private void Rename_Click(object sender, RoutedEventArgs e) => _active.BeginRename();

    private void Delete_Click(object sender, RoutedEventArgs e) => _active.Delete(permanently: Native.IsKeyDown(0x10));

    private void CopyOther_Click(object sender, RoutedEventArgs e) => _active.TransferToOtherPane(move: false);

    private void MoveOther_Click(object sender, RoutedEventArgs e) => _active.TransferToOtherPane(move: true);

    private void ViewMenu_Opening(object sender, object e)
    {
        bool thumbs = _active.Tab.View == "thumbnails";
        ViewDetailsItem.IsChecked = !thumbs;
        ViewThumbsItem.IsChecked = thumbs;
        HiddenItem.IsChecked = Settings.ShowHidden;
        ExtensionsItem.IsChecked = Settings.ShowExtensions;
        ColorfulItem.IsChecked = Settings.ColorfulNames;
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e) => _active.SetView("details");

    private void ViewThumbs_Click(object sender, RoutedEventArgs e) => _active.SetView("thumbnails");

    private void Hidden_Click(object sender, RoutedEventArgs e) => ToggleSetting(s => s.ShowHidden = !s.ShowHidden, reload: true);

    private void Extensions_Click(object sender, RoutedEventArgs e) => ToggleSetting(s => s.ShowExtensions = !s.ShowExtensions, reload: false);

    private void Colorful_Click(object sender, RoutedEventArgs e) => ToggleSetting(s => s.ColorfulNames = !s.ColorfulNames, reload: false);

    private void ToggleSetting(Action<AppSettings> change, bool reload)
    {
        change(Settings);
        App.Instance!.SaveSettings();
        foreach (MainWindow window in App.Instance.Windows)
        {
            window.LeftPane.ApplyDisplaySettings(reload);
            window.RightPane.ApplyDisplaySettings(reload);
        }
    }

    private void DualToggle_Click(object sender, RoutedEventArgs e)
    {
        Settings.DualPane = !Settings.DualPane;
        ApplyLayout();
        App.Instance!.SaveSettings();
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Settings.ShowPreview) Settings.PreviewWidth = PreviewColumn.ActualWidth;
        Settings.ShowPreview = !Settings.ShowPreview;
        ApplyLayout();
        App.Instance!.SaveSettings();
    }

    // ---------------------------------------------------------------- splitters

    private void SidebarSplitter_Dragged(object? sender, double delta) =>
        SidebarColumn.Width = new GridLength(Math.Clamp(SidebarColumn.ActualWidth + delta, 160, 480));

    private void PreviewSplitter_Dragged(object? sender, double delta) =>
        PreviewColumn.Width = new GridLength(Math.Clamp(PreviewColumn.ActualWidth - delta, 220, 900));

    private void PaneSplitter_Dragged(object? sender, double delta)
    {
        double total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        if (total <= 0) return;
        double left = Math.Clamp(LeftColumn.ActualWidth + delta, 260, total - 260);
        LeftColumn.Width = new GridLength(left, GridUnitType.Star);
        RightColumn.Width = new GridLength(total - left, GridUnitType.Star);
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>F8 moves the selection to the other pane. F7 (copy) comes through the keyboard hook instead.</summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsViewerOpen)
        {
            Viewer.HandleKey(e);
            return;
        }
        if (e.Key != VirtualKey.F8) return;
        if (e.OriginalSource is TextBox or AutoSuggestBox or PasswordBox) return;
        _active.TransferToOtherPane(move: true);
        e.Handled = true;
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Viewer.Visibility == Visibility.Visible) return;
        bool ctrl = Native.IsKeyDown(0x11), shift = Native.IsKeyDown(0x10), alt = Native.IsKeyDown(0x12);
        bool inText = e.OriginalSource is TextBox or AutoSuggestBox or PasswordBox;
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.T when ctrl:
                _active.OpenTab(_active.Location);
                break;
            case VirtualKey.W when ctrl:
                _active.CloseCurrentTab();
                break;
            case VirtualKey.Tab when ctrl:
                _active.NextTab(shift ? -1 : 1);
                break;
            case VirtualKey.Tab when !inText && Settings.DualPane && e.OriginalSource is ListViewItem or GridViewItem or ListViewBase:
                FilePane other = OtherPane(_active)!;
                SetActivePane(other);
                other.FocusList();
                break;
            case VirtualKey.N when ctrl && shift:
                _active.NewFolder();
                break;
            case VirtualKey.N when ctrl:
                App.Instance!.OpenWindow(_active.Folder);
                break;
            case VirtualKey.Left when alt:
                _active.GoBack();
                break;
            case VirtualKey.Right when alt:
                _active.GoForward();
                break;
            case VirtualKey.Up when alt:
                _active.GoUp();
                break;
            case VirtualKey.L when ctrl:
            case VirtualKey.D when alt:
            case VirtualKey.F4 when !alt:
                _active.FocusAddress();
                break;
            case VirtualKey.F when ctrl:
            case VirtualKey.E when ctrl:
            case VirtualKey.F3:
                _active.FocusSearch();
                break;
            case VirtualKey.F5:
                await _active.ReloadAsync();
                break;
            case VirtualKey.C when ctrl && shift && !inText:
                _active.CopyPaths();
                break;
            case VirtualKey.C when ctrl && !inText:
                _active.Copy(cut: false);
                break;
            case VirtualKey.X when ctrl && !inText:
                _active.Copy(cut: true);
                break;
            case VirtualKey.V when ctrl && !inText:
                _active.Paste();
                break;
            case VirtualKey.F9:
                DualToggle_Click(this, new RoutedEventArgs());
                break;
            case VirtualKey.P when alt:
                PreviewToggle_Click(this, new RoutedEventArgs());
                break;
            case VirtualKey.H when ctrl && !inText:
                Hidden_Click(this, new RoutedEventArgs());
                break;
            case VirtualKey.Number1 when ctrl && shift:
                _active.SetView("thumbnails");
                break;
            case VirtualKey.Number6 when ctrl && shift:
                _active.SetView("details");
                break;
            case VirtualKey.F10 when shift && !inText:
            case VirtualKey.Application when !inText:
                _active.ShowNativeMenu();
                break;
            case (VirtualKey)188 when ctrl:
                OpenSettings(aiSection: false);
                break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
    }
}

/// <summary>A one-shot timer that starts over on every <see cref="Restart"/>.</summary>
internal sealed class DispatcherQueueTimerWrapper
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherQueueTimerWrapper(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = false;
        _timer.SafeTick(tick);
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
