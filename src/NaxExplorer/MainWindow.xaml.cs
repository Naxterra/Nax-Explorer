using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

    /// <summary>Entries that Windows shows in its own Explorer, such as the Recycle Bin.</summary>
    public bool OpensInExplorer { get; init; }
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

        App.Instance!.Search.StatusChanged += _ => DispatcherQueue.SafeEnqueue(UpdateAiCard);
        App.Instance.Search.AvailabilityChanged += () => DispatcherQueue.SafeEnqueue(UpdateAiCard);
        Closed += (_, _) => SaveState();
        bool firstActivation = true;
        Activated += (_, args) =>
        {
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
        CopyOtherButton.IsEnabled = MoveOtherButton.IsEnabled = dual;
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
            _previewTimer.Restart();
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

    internal void OnPaneLocationChanged(FilePane pane)
    {
        if (pane == _active) UpdateTitle();
    }

    internal void OnPaneSelectionChanged(FilePane pane)
    {
        if (pane == _active) _previewTimer.Restart();
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
            foreach (FilePane pane in new[] { window.LeftPane, window.RightPane })
            {
                if (pane.Folder is { } shown && PathTools.SameLocation(shown, folder)) pane.ScheduleRefresh();
            }
        }
    }

    internal void SetCutPaths(IReadOnlyList<string> paths)
    {
        _cut = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        LeftPane.UpdateCutMarks();
        RightPane.UpdateCutMarks();
    }

    private void UpdatePreview()
    {
        if (!Settings.ShowPreview) return;
        IReadOnlyList<Models.FileItem> selected = _active.SelectedItems;
        Preview.Show(selected.Count == 1 ? selected[0] : null, selected.Count, _active.Folder ?? _active.FolderOrSearchRoot);
    }

    internal void ShowViewer(IReadOnlyList<string> pictures, int index, FilePane source)
    {
        Viewer.Open(pictures, index, source);
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
        var quick = new List<SidebarEntry>
        {
            new() { Glyph = "", Label = Loc.T("Home"), Location = profile, Brush = Color(0x3B, 0x82, 0xF6) },
            new() { Glyph = "", Label = Loc.T("Desktop"), Location = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Brush = Color(0x06, 0xB6, 0xD4) },
            new() { Glyph = "", Label = Loc.T("Downloads"), Location = Path.Combine(profile, "Downloads"), Brush = Color(0x10, 0xB9, 0x81) },
            new() { Glyph = "", Label = Loc.T("Documents"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Brush = Color(0x63, 0x66, 0xF1) },
            new() { Glyph = "", Label = Loc.T("Pictures"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Brush = Color(0xEC, 0x48, 0x99) },
            new() { Glyph = "", Label = Loc.T("Music"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Brush = Color(0xF5, 0x9E, 0x0B) },
            new() { Glyph = "", Label = Loc.T("Videos"), Location = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Brush = Color(0x8B, 0x5C, 0xF6) },
            new() { Glyph = "", Label = Loc.T("RecycleBin"), Location = "shell:RecycleBinFolder", Brush = Color(0x94, 0xA3, 0xB8), OpensInExplorer = true },
        };
        QuickList.ItemsSource = quick.Where(e => e.OpensInExplorer || Directory.Exists(e.Location)).ToList();
        Brush folderBrush = new SolidColorBrush(Palette.Of(FileCategory.Folder, dark));
        // The stored names show at once; Windows is asked in the background, because a pinned share that does not
        // answer can take seconds and must not freeze the window.
        List<SidebarEntry> pinned = Settings.Pinned.Select(p => new SidebarEntry { Glyph ="", Label = ShellNames.Known(p), Location = p, Brush = folderBrush }).ToList();
        PinnedList.ItemsSource = pinned;
        PinnedHeader.Visibility = PinnedList.Visibility = Settings.Pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pinned.Count > 0) _ = NamePinnedAsync(pinned);
        _ = LoadDrivesAsync();
    }

    private async Task NamePinnedAsync(List<SidebarEntry> pinned)
    {
        string[] names = await Task.Run(() => pinned.Select(e => ShellNames.OfFolder(e.Location)).ToArray());
        if (!ReferenceEquals(PinnedList.ItemsSource, pinned) || pinned.Select(e => e.Label).SequenceEqual(names)) return;
        PinnedList.ItemsSource = pinned.Select((e, i) => new SidebarEntry { Glyph = e.Glyph, Label = names[i], Location = e.Location, Brush = e.Brush }).ToList();
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
        }).ToList();
    }

    private async void Sidebar_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarEntry entry) return;
        if (entry.OpensInExplorer)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", entry.Location) { UseShellExecute = true });
            return;
        }
        await _active.NavigateAsync(entry.Location);
    }

    /// <summary>A middle click opens the entry in a new tab.</summary>
    private void Sidebar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarEntry { OpensInExplorer: false } entry)
        {
            _active.OpenTab(entry.Location);
            e.Handled = true;
        }
    }

    private void PinnedList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarEntry entry) return;
        var menu = new MenuFlyout();
        var unpin = new MenuFlyoutItem { Text = Loc.T("Unpin"), Icon = new FontIcon { Glyph = "" } };
        unpin.Click += (_, _) =>
        {
            Settings.Pinned.RemoveAll(p => PathTools.SameLocation(p, entry.Location));
            App.Instance!.SaveSettings();
            foreach (MainWindow window in App.Instance.Windows) window.BuildSidebar();
        };
        var newTab = new MenuFlyoutItem { Text = Loc.T("OpenInNewTab"), Icon = new FontIcon { Glyph = "" } };
        newTab.Click += (_, _) => _active.OpenTab(entry.Location);
        menu.Items.Add(newTab);
        menu.Items.Add(unpin);
        menu.ShowAt((FrameworkElement)e.OriginalSource);
        e.Handled = true;
    }

    internal void Pin(string folder)
    {
        if (Settings.Pinned.Any(p => PathTools.SameLocation(p, folder))) return;
        Settings.Pinned.Add(folder);
        App.Instance!.SaveSettings();
        foreach (MainWindow window in App.Instance.Windows) window.BuildSidebar();
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarEntry { OpensInExplorer: false } entry
            && (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Properties.ContainsKey("NaxExplorer.Paths")))
        {
            bool move = e.Modifiers.HasFlag(DragDropModifiers.Shift);
            e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.F(move ? "DropMove" : "DropCopy", entry.Label);
            return;
        }
        e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void Sidebar_Drop(object sender, DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SidebarEntry { OpensInExplorer: false } entry) return;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            List<string> paths = e.DataView.Properties.TryGetValue("NaxExplorer.Paths", out object? value) && value is string[] own ? [.. own] : [];
            if (paths.Count == 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            bool move = e.Modifiers.HasFlag(DragDropModifiers.Shift);
            if (paths.Count == 0 || TransferCheck.Check(paths, entry.Location, move) != TransferProblem.None) return;
            if (move) await ShellActions.MoveAsync(paths, entry.Location, Hwnd);
            else await ShellActions.CopyAsync(paths, entry.Location, Hwnd);
            RefreshPanesShowing(entry.Location);
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
        if (e.Key != VirtualKey.F8 || Viewer.Visibility == Visibility.Visible) return;
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
}
