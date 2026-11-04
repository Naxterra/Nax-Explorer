using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Core;
using NaxExplorer.Models;
using NaxExplorer.Search;
using NaxExplorer.Services;
using NaxExplorer.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using FileAttributes = System.IO.FileAttributes;

namespace NaxExplorer.Controls;

/// <summary>An entry of the address bar. The bar shows <see cref="ToString"/>.</summary>
internal sealed class Crumb(string label, string location)
{
    public string Location { get; } = location;

    public override string ToString() => label;
}

/// <summary>
/// One side of the window: tabs, address bar, filter and search, and the file list in a details or thumbnail view.
/// Folder contents are read off the UI thread and kept current by a folder watcher that updates single rows.
/// </summary>
public sealed partial class FilePane : UserControl
{
    private const string InternalDragFormat = "NaxExplorer.Paths";

    private readonly List<PaneTab> _tabs = [];
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _flushTimer;
    private readonly ConcurrentQueue<SearchHit> _pendingHits = new();
    private MainWindow _owner = null!;
    private PaneTab _tab = null!;
    private ObservableCollection<FileItem> _items = [];
    private List<FileItem> _all = [];
    private List<FileEntry> _entries = [];
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private SearchCounters? _counters;
    private readonly Stopwatch _searchWatch = new();
    private string _filter = "";
    private string _typed = "";
    private DateTime _typedAt;
    private string? _selectAfterRefresh;
    private string _freeSpace = "";
    private bool _switchingTabs;

    public FilePane()
    {
        InitializeComponent();
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.SafeTick(() => _ = RefreshAsync());
        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
        _flushTimer.SafeTick(FlushHits);
        // The list marks pointer presses as handled, so the pane listens to handled ones too, to know it is active.
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
        List.AddHandler(KeyDownEvent, new KeyEventHandler(List_PreviewSpace), handledEventsToo: true);
    }

    // ---------------------------------------------------------------- public surface

    internal PaneTab Tab => _tab;

    internal string Location => _tab.Location;

    /// <summary>The folder shown, or null on "This PC" and while search results are shown.</summary>
    internal string? Folder => _tab.Search is null && !PathTools.IsThisPc(_tab.Location) ? _tab.Location : null;

    /// <summary>The folder the pane is in, also while search results are shown.</summary>
    internal string? FolderOrSearchRoot => PathTools.IsThisPc(_tab.Location) ? null : _tab.Location;

    internal bool IsSearchOpen => _tab.Search is not null;

    internal IReadOnlyList<FileItem> SelectedItems => CurrentList.SelectedItems.OfType<FileItem>().ToList();

    internal IReadOnlyList<string> SelectedPaths => SelectedItems.Where(i => i.Entry.Kind != EntryKind.Drive).Select(i => i.Entry.FullPath).ToList();

    internal IReadOnlyList<FileItem> VisibleItems => _items;

    private ListViewBase CurrentList => _tab.View == "thumbnails" && !PathTools.IsThisPc(_tab.Location) && _tab.Search is null ? Thumbs : List;

    private AppSettings Settings => App.Instance!.Settings;

    private ListingOptions Listing => new(Settings.ShowHidden, Settings.ShowProtectedFiles);

    private ItemStyle RowStyle => new(_owner.IsDark, Settings.ColorfulNames, Settings.ShowExtensions);

    private int IconPixels => (int)Math.Ceiling(20 * (XamlRoot?.RasterizationScale ?? 1));

    private int ThumbnailPixels => Math.Min(256, (int)Math.Ceiling(128 * (XamlRoot?.RasterizationScale ?? 1)));

    internal void Initialize(MainWindow owner, PaneSettings settings, string? start, string fallback)
    {
        _owner = owner;
        foreach (string location in settings.Tabs)
        {
            if (PathTools.IsThisPc(location) || Directory.Exists(location)) AddTab(location, settings, select: false);
        }
        if (start is not null)
        {
            string folder = Directory.Exists(start) ? start : Path.GetDirectoryName(start) ?? fallback;
            AddTab(folder, settings, select: false).Selection = File.Exists(start) ? [start] : [];
        }
        if (_tabs.Count == 0) AddTab(fallback, settings, select: false);
        int active = start is not null ? _tabs.Count - 1 : Math.Clamp(settings.ActiveTab, 0, _tabs.Count - 1);
        Tabs.SelectedItem = _tabs[active].Header;
        if (_tab is null) ShowTab(_tabs[active]);
    }

    internal PaneSettings Save() => new()
    {
        Tabs = _tabs.Select(t => t.Location).ToList(),
        ActiveTab = Math.Max(0, _tabs.IndexOf(_tab)),
        View = _tab.View,
        Sort = _tab.Sort,
        Descending = _tab.Descending,
    };

    internal void SetActive(bool active)
    {
        ActiveBar.Opacity = active ? 1 : 0;
        ActiveBackground.Opacity = active ? 1 : 0;
    }

    internal void FocusList()
    {
        ListViewBase list = CurrentList;
        // Right after the list was filled its rows do not exist yet; a layout pass creates them, so one can take focus.
        list.UpdateLayout();
        if (list.SelectedIndex < 0 && _items.Count > 0)
        {
            // Focus goes to the first row without selecting it, as in Explorer.
            if (list.ContainerFromIndex(0) is ListViewItem first)
            {
                first.Focus(FocusState.Programmatic);
                return;
            }
        }
        else if (list.SelectedItem is { } selected && list.ContainerFromItem(selected) is Control container)
        {
            container.Focus(FocusState.Programmatic);
            return;
        }
        list.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Focuses the list after the current layout pass. A new row template rebuilds every row after it is set, which
    /// would take the focus away from a row focused at once.
    /// </summary>
    internal void FocusListSoon() => DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, FocusList);

    internal void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    internal void FocusAddress() => EnterAddressEdit();

    // ---------------------------------------------------------------- tabs

    private PaneTab AddTab(string location, PaneSettings? defaults, bool select)
    {
        var tab = new PaneTab(location)
        {
            View = defaults?.View ?? _tab?.View ?? "details",
            Sort = defaults?.Sort ?? _tab?.Sort ?? SortColumn.Name,
            Descending = defaults?.Descending ?? _tab?.Descending ?? false,
        };
        var header = new TabViewItem
        {
            Header = PlaceName(location),
            IconSource = new FontIconSource { Glyph = PathTools.IsThisPc(location) ? "" : "", Foreground = new SolidColorBrush(Palette.Of(FileCategory.Folder, _owner?.IsDark ?? true)) },
            Tag = tab,
        };
        tab.Header = header;
        _tabs.Add(tab);
        Tabs.TabItems.Add(header);
        if (select) Tabs.SelectedItem = header;
        return tab;
    }

    internal void OpenTab(string location, bool select = true)
    {
        string? folder = location;
        string? file = null;
        if (!PathTools.IsThisPc(location) && File.Exists(location))
        {
            file = location;
            folder = Path.GetDirectoryName(location);
        }
        if (folder is null) return;
        PaneTab tab = AddTab(folder, null, select);
        if (file is not null) tab.Selection = [file];
    }

    internal void CloseTab(PaneTab tab)
    {
        if (_tabs.Count == 1)
        {
            // Closing the last tab closes the pane, or the window when it is the only pane, as in Explorer.
            _owner.CloseLastTab(this);
            return;
        }
        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Tabs.TabItems.Remove(tab.Header);
        if (tab == _tab) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)].Header;
    }

    internal void CloseCurrentTab() => CloseTab(_tab);

    /// <summary>The places of all tabs and the index of the shown one, for handing them to the other pane.</summary>
    internal (List<string> Locations, int Active) TabLocations() => (_tabs.Select(t => t.Location).ToList(), Math.Max(0, _tabs.IndexOf(_tab)));

    /// <summary>Replaces all tabs with tabs for these places, for example the other pane's tabs when this one takes over.</summary>
    internal void ResetTabs(IReadOnlyList<string> locations, int active)
    {
        if (locations.Count == 0) return;
        CancelSearch();
        _tabs.Clear();
        Tabs.TabItems.Clear();
        foreach (string location in locations) AddTab(location, null, select: false);
        Tabs.SelectedItem = _tabs[Math.Clamp(active, 0, _tabs.Count - 1)].Header;
    }

    internal void NextTab(int step)
    {
        int index = (_tabs.IndexOf(_tab) + step + _tabs.Count) % _tabs.Count;
        Tabs.SelectedItem = _tabs[index].Header;
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => OpenTab(_tab.Location);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is PaneTab tab) CloseTab(tab);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: PaneTab tab } && tab != _tab) ShowTab(tab);
    }

    private void ShowTab(PaneTab tab)
    {
        if (_tab is not null && _tab != tab)
        {
            _tab.Selection = SelectedPaths.Take(500).ToList();
            CancelSearch();
        }
        _switchingTabs = true;
        _tab = tab;
        _filter = "";
        SearchBox.Text = tab.Search?.Query ?? "";
        _switchingTabs = false;
        if (tab.Search is not null) OpenSearchUi(run: true);
        else
        {
            SearchBar.Visibility = Visibility.Collapsed;
            _ = LoadAsync();
        }
    }

    private void UpdateTabHeader()
    {
        if (_tab.Header is null) return;
        _tab.Header.Header = _tab.Search is { } search ? $"“{search.Query}”" : PlaceName(_tab.Location);
        ((FontIconSource)_tab.Header.IconSource).Glyph = _tab.Search is not null ? "" : PathTools.IsThisPc(_tab.Location) ? "" : "";
        ToolTipService.SetToolTip(_tab.Header, PathTools.IsThisPc(_tab.Location) ? Loc.T("ThisPC") : _tab.Location);
    }

    // ---------------------------------------------------------------- navigation

    internal async Task NavigateAsync(string location, string? select = null, bool record = true)
    {
        if (!PathTools.IsThisPc(location) && !Directory.Exists(location))
        {
            _owner.ShowMessage(Loc.F("FolderNotFound", location), InfoBarSeverity.Warning);
            return;
        }
        if (_tab.Search is not null) CloseSearchUi();
        if (record) _tab.History.Visit(location);
        _tab.Location = location;
        _tab.Selection = select is null ? [] : [select];
        _filter = "";
        SearchBox.Text = "";
        await LoadAsync();
        if (_owner.ActivePane == this) FocusListSoon();
    }

    internal async void GoBack()
    {
        if (_tab.Search is not null)
        {
            CloseSearch();
            return;
        }
        string from = _tab.Location;
        if (_tab.History.GoBack() is { } back) await NavigateAsync(back, PathTools.IsSameOrInside(from, back) ? ChildOf(from, back) : null, record: false);
    }

    internal async void GoForward()
    {
        if (_tab.History.GoForward() is { } forward) await NavigateAsync(forward, record: false);
    }

    internal async void GoUp()
    {
        if (_tab.Search is not null)
        {
            CloseSearch();
            return;
        }
        string from = _tab.Location;
        if (PathTools.Parent(from) is { } parent) await NavigateAsync(parent, PathTools.IsThisPc(parent) ? Path.GetPathRoot(from) : from);
    }

    /// <summary>The direct child of <paramref name="ancestor"/> on the way to <paramref name="path"/>, to select it after going up.</summary>
    private static string? ChildOf(string path, string ancestor)
    {
        string? current = path;
        while (current is not null)
        {
            string? parent = Path.GetDirectoryName(current);
            if (parent is not null && PathTools.SameLocation(parent, ancestor)) return current;
            current = parent;
        }
        return null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    internal async Task ReloadAsync()
    {
        if (_tab.Search is not null)
        {
            await RunSearchAsync();
            return;
        }
        _tab.Selection = SelectedPaths.Take(500).ToList();
        ImageCache.ClearThumbnails();
        await LoadAsync();
    }

    /// <summary>The name of a place as Windows shows it: "Dieser PC", "Lokaler Datenträger (C:)", "Benutzer".</summary>
    internal static string PlaceName(string location) => PathTools.IsThisPc(location) ? Loc.T("ThisPC") : ShellNames.Known(location);

    /// <summary>
    /// The folder a found item lies in, written with the names Windows shows. Search threads pass ask: true so the
    /// names are stored; the UI thread passes ask: false and only reads the stored names.
    /// </summary>
    private static string ShownFolder(string fullPath, bool ask)
    {
        string? folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder)) return "";
        return ask ? ShellNames.PathOf(folder) : ShellNames.KnownPath(folder);
    }

    /// <summary>The filter matches the file system name and the shown name, so "Bilder" finds the Pictures folder.</summary>
    private bool Matches(FileEntry entry) =>
        entry.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || entry.DisplayName.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows the address bar with the names Windows uses. Names not known yet are asked from Windows in the background,
    /// because a disconnected network drive can take seconds to answer, and the bar is filled again when they arrive.
    /// </summary>
    private void ShowCrumbs()
    {
        string location = _tab.Location;
        List<Core.PathSegment> segments = PathTools.Segments(location, Loc.T("ThisPC"));
        Breadcrumb.ItemsSource = segments.Select(s => new Crumb(PlaceName(s.Location), s.Location)).ToList();
        Task.Run(() =>
        {
            // The folder names come first. The drive's own name is asked again only for a local drive, which answers at
            // once and may hold another stick than before; a network drive that answers slowly would otherwise hold back
            // the bar on every step, so it keeps the name This PC last read for it.
            foreach (Core.PathSegment segment in segments.Skip(2)) ShellNames.OfFolder(segment.Location);
            if (segments.Count > 1 && ShellNames.IsLocalDrive(segments[1].Location)) ShellNames.Of(segments[1].Location);
        }).ContinueWith(_ => DispatcherQueue.SafeEnqueue(() =>
        {
            if (!PathTools.SameLocation(location, _tab.Location)) return;
            Breadcrumb.ItemsSource = segments.Select(s => new Crumb(PlaceName(s.Location), s.Location)).ToList();
            UpdateTabHeader();
            _owner.OnPaneLocationChanged(this);
            DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true));
        }), TaskScheduler.Default);
    }

    private void UpdateChrome()
    {
        BackButton.IsEnabled = _tab.History.CanGoBack || _tab.Search is not null;
        ForwardButton.IsEnabled = _tab.History.CanGoForward;
        UpButton.IsEnabled = PathTools.Parent(_tab.Location) is not null || _tab.Search is not null;
        ShowCrumbs();
        DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true));
        UpdateTabHeader();
        UpdateHeaders();
        bool thumbs = _tab.View == "thumbnails";
        DetailsToggle.IsChecked = !thumbs;
        ThumbsToggle.IsChecked = thumbs;
        _owner.OnPaneLocationChanged(this);
    }

    // ---------------------------------------------------------------- loading

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        PaneTab tab = _tab;
        string location = tab.Location;
        StopWatcher();
        UpdateChrome();
        DispatcherQueueTimer slow = DispatcherQueue.CreateTimer();
        slow.Interval = TimeSpan.FromMilliseconds(200);
        slow.IsRepeating = false;
        slow.SafeTick(() => LoadingBar.Visibility = Visibility.Visible);
        slow.Start();
        List<FileEntry> entries;
        string? error = null;
        ListingOptions listing = Listing;
        try
        {
            entries = await Task.Run(() => PathTools.IsThisPc(location) ? DirectoryReader.ReadDrives() : DirectoryReader.Read(location, listing, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            entries = [];
            error = Loc.T("AccessDenied");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Security.SecurityException)
        {
            entries = [];
            error = ex.Message;
        }
        finally
        {
            slow.Stop();
            if (_loadCts == cts) LoadingBar.Visibility = Visibility.Collapsed;
        }
        if (cts.IsCancellationRequested || tab != _tab || !ReferenceEquals(location, tab.Location)) return;
        _entries = entries;
        ShowEntries(tab.Selection);
        if (error is not null) ShowEmpty("", error);
        StartWatcher(location);
        UpdateFreeSpace(location);
        if (FocusAfterFirstLoad)
        {
            FocusAfterFirstLoad = false;
            FocusListSoon();
        }
    }

    /// <summary>Set by the window for the pane that should take the keyboard focus when its first folder is shown.</summary>
    internal bool FocusAfterFirstLoad { get; set; }

    private void ShowEntries(IReadOnlyList<string> select)
    {
        var sorted = _entries.ToList();
        EntrySorter.Sort(sorted, _tab.Sort, _tab.Descending);
        ItemStyle style = RowStyle;
        HashSet<string> cut = _owner.CutPaths;
        _all = sorted.Select(e => new FileItem(e, style) { IsCut = cut.Contains(e.FullPath) }).ToList();
        ApplyFilter(select);
    }

    private void ApplyFilter(IReadOnlyList<string>? select)
    {
        IEnumerable<FileItem> visible = _filter.Length == 0 ? _all : _all.Where(i => Matches(i.Entry));
        _items = new ObservableCollection<FileItem>(visible);
        BindList();
        if (select is { Count: > 0 }) Select(select);
        UpdateEmpty();
        UpdateStatus();
    }

    private void BindList()
    {
        bool thisPc = PathTools.IsThisPc(_tab.Location) && _tab.Search is null;
        bool thumbs = CurrentList == Thumbs;
        List.ItemTemplate = (DataTemplate)Resources[_tab.Search is not null ? "ResultTemplate" : thisPc ? "DriveTemplate" : "DetailsTemplate"];
        List.Visibility = thumbs ? Visibility.Collapsed : Visibility.Visible;
        Thumbs.Visibility = thumbs ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeader.Visibility = thumbs || thisPc ? Visibility.Collapsed : Visibility.Visible;
        ((ListViewBase)(thumbs ? List : Thumbs)).ItemsSource = null;
        CurrentList.ItemsSource = _items;
    }

    private void Select(IReadOnlyList<string> paths)
    {
        ListViewBase list = CurrentList;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        FileItem? first = null;
        foreach (FileItem item in _items)
        {
            if (!wanted.Contains(item.Entry.FullPath)) continue;
            first ??= item;
            list.SelectedItems.Add(item);
        }
        if (first is not null) list.ScrollIntoView(first, ScrollIntoViewAlignment.Default);
    }

    private void ShowEmpty(string glyph, string text)
    {
        EmptyGlyph.Glyph = glyph;
        EmptyText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    private void UpdateEmpty()
    {
        if (_items.Count > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_tab.Search is not null) ShowEmpty("", _searchCts is null ? Loc.T("NoResults") : "");
        else if (_filter.Length > 0) ShowEmpty("", Loc.T("NothingMatchesFilter"));
        else ShowEmpty("", Loc.T("EmptyFolder"));
    }

    private void UpdateFreeSpace(string location)
    {
        _freeSpace = "";
        if (PathTools.IsThisPc(location)) return;
        string? root = Path.GetPathRoot(location);
        if (string.IsNullOrEmpty(root)) return;
        Task.Run(() =>
        {
            try
            {
                var drive = new DriveInfo(root);
                return drive.IsReady ? Loc.F("FreeSpace", SizeFormat.Format(drive.AvailableFreeSpace)) : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "";
            }
        }).ContinueWith(task =>
        {
            DispatcherQueue.SafeEnqueue(() =>
            {
                if (!PathTools.SameLocation(_tab.Location, location)) return;
                _freeSpace = task.Result;
                UpdateStatus();
            });
        }, TaskScheduler.Default);
    }

    internal void UpdateStatus()
    {
        if (_tab.Search is not null)
        {
            StatusText.Text = Loc.F("ResultCount", _items.Count);
            return;
        }
        EngineBadge.Visibility = Visibility.Collapsed;
        var parts = new List<string> { Loc.F("ItemCount", _items.Count) };
        IReadOnlyList<FileItem> selected = SelectedItems;
        if (selected.Count > 0)
        {
            long bytes = selected.Where(i => i.Entry.Kind == EntryKind.File).Sum(i => i.Entry.Size);
            parts.Add(bytes > 0 ? Loc.F("SelectedWithSize", selected.Count, SizeFormat.Format(bytes)) : Loc.F("Selected", selected.Count));
        }
        if (_freeSpace.Length > 0) parts.Add(_freeSpace);
        StatusText.Text = string.Join("   ·   ", parts);
    }

    /// <summary>Applies changed display settings (hidden files, extensions, colours, theme) to every row.</summary>
    internal async void ApplyDisplaySettings(bool reload)
    {
        if (reload && _tab.Search is null)
        {
            _tab.Selection = SelectedPaths.Take(500).ToList();
            await LoadAsync();
            return;
        }
        ItemStyle style = RowStyle;
        foreach (FileItem item in _all) item.Restyle(style);
        foreach (PaneTab tab in _tabs) ((FontIconSource)tab.Header!.IconSource).Foreground = new SolidColorBrush(Palette.Of(FileCategory.Folder, _owner.IsDark));
    }

    internal void UpdateCutMarks()
    {
        HashSet<string> cut = _owner.CutPaths;
        foreach (FileItem item in _all) item.IsCut = cut.Contains(item.Entry.FullPath);
    }

    // ---------------------------------------------------------------- live updates

    private void StartWatcher(string location)
    {
        if (PathTools.IsThisPc(location)) return;
        try
        {
            var watcher = new FileSystemWatcher(location)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => DispatcherQueue.SafeEnqueue(ScheduleRefresh);
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => DispatcherQueue.SafeEnqueue(ScheduleRefresh);
            watcher.Error += (_, _) => DispatcherQueue.SafeEnqueue(ScheduleRefresh);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Without a watcher the folder is still current after F5.
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer.Stop();
    }

    internal void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Reads the folder again and updates only the rows that changed, keeping the selection and scroll position.</summary>
    private async Task RefreshAsync()
    {
        if (_tab.Search is not null) return;
        PaneTab tab = _tab;
        string location = tab.Location;
        ListingOptions listing = Listing;
        List<FileEntry> fresh;
        try
        {
            fresh = await Task.Run(() => PathTools.IsThisPc(location) ? DirectoryReader.ReadDrives() : DirectoryReader.Read(location, listing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder itself was deleted or renamed: go to the nearest folder that still exists.
            string? up = location;
            while (up is not null && !PathTools.IsThisPc(up) && !Directory.Exists(up)) up = PathTools.Parent(up);
            if (tab == _tab && up is not null && !PathTools.SameLocation(up, location)) await NavigateAsync(up);
            return;
        }
        if (tab != _tab || !PathTools.SameLocation(location, tab.Location) || tab.Search is not null) return;
        FolderChanges changes = FolderChanges.Compute(_entries, fresh);
        _entries = fresh;
        if (!changes.IsEmpty)
        {
            if (changes.Added.Count + changes.Removed.Count > 400)
            {
                ShowEntries(SelectedPaths.ToList());
            }
            else
            {
                ApplyChanges(changes);
            }
        }
        if (_selectAfterRefresh is { } pending && _items.FirstOrDefault(i => PathTools.SameLocation(i.Entry.FullPath, pending)) is { } target)
        {
            _selectAfterRefresh = null;
            CurrentList.SelectedItems.Clear();
            CurrentList.SelectedItem = target;
            CurrentList.ScrollIntoView(target);
        }
        UpdateEmpty();
        UpdateStatus();
    }

    private void ApplyChanges(FolderChanges changes)
    {
        var byPath = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (FileItem item in _all) byPath[item.Entry.FullPath] = item;
        foreach (FileEntry removed in changes.Removed)
        {
            if (!byPath.Remove(removed.FullPath, out FileItem? item)) continue;
            _all.Remove(item);
            _items.Remove(item);
        }
        foreach (FileEntry changed in changes.Changed)
        {
            if (byPath.TryGetValue(changed.FullPath, out FileItem? item)) item.Update(changed);
        }
        Comparison<FileEntry> order = EntrySorter.Comparison(_tab.Sort, _tab.Descending);
        ItemStyle style = RowStyle;
        HashSet<string> cut = _owner.CutPaths;
        foreach (FileEntry added in changes.Added)
        {
            var item = new FileItem(added, style) { IsCut = cut.Contains(added.FullPath) };
            _all.Insert(SortedIndex(_all, added, order), item);
            if (_filter.Length == 0 || Matches(added)) _items.Insert(SortedIndex(_items, added, order), item);
        }
    }

    private static int SortedIndex(IList<FileItem> list, FileEntry entry, Comparison<FileEntry> order)
    {
        int low = 0, high = list.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (order(list[middle].Entry, entry) <= 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    // ---------------------------------------------------------------- sorting and views

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out SortColumn column)) return;
        SetSort(column, column == _tab.Sort ? !_tab.Descending : column is SortColumn.Modified or SortColumn.Size);
    }

    internal void SetSort(SortColumn column, bool descending)
    {
        _tab.Sort = column;
        _tab.Descending = descending;
        UpdateHeaders();
        if (_tab.Search is not null) return;
        IReadOnlyList<string> selection = SelectedPaths.ToList();
        ShowEntries(selection);
    }

    private void UpdateHeaders()
    {
        string Mark(SortColumn column) => column == _tab.Sort ? (_tab.Descending ? "  ↓" : "  ↑") : "";
        NameHeader.Content = Loc.T("ColumnName") + Mark(SortColumn.Name);
        DateHeader.Content = Loc.T("ColumnModified") + Mark(SortColumn.Modified);
        TypeHeader.Content = Loc.T("ColumnType") + Mark(SortColumn.Type);
        SizeHeader.Content = Loc.T("ColumnSize") + Mark(SortColumn.Size);
        bool search = _tab.Search is not null;
        NameHeader.IsEnabled = DateHeader.IsEnabled = SizeHeader.IsEnabled = !search;
        ApplyHeaderColumns();
    }

    // ---------------------------------------------------------------- column widths

    private (double Date, double Type, double Size) _columns = (130, 130, 90);

    /// <summary>
    /// Fits the columns to the pane: in a narrow pane the type column goes first, then the date, then the size, so the
    /// name always keeps room to be read.
    /// </summary>
    private void ContentArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double available = e.NewSize.Width - 28 - 40;
        double date = 125, type = 130, size = 80;
        if (available - date - type - size < 220) type = 0;
        if (available - date - size < 160) date = 0;
        if (available - size < 140) size = 0;
        if (_columns == (date, type, size)) return;
        _columns = (date, type, size);
        ApplyHeaderColumns();
        if (List.ItemsPanelRoot is { } panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is SelectorItem { ContentTemplateRoot: Grid grid }) ApplyColumns(grid);
            }
        }
    }

    private void ApplyHeaderColumns()
    {
        bool search = _tab?.Search is not null;
        DateColumn.Width = new GridLength(_columns.Date);
        TypeColumn.Width = new GridLength(search ? 0 : _columns.Type);
        SizeColumn.Width = new GridLength(_columns.Size);
        DateHeader.Visibility = _columns.Date > 0 ? Visibility.Visible : Visibility.Collapsed;
        TypeHeader.Visibility = !search && _columns.Type > 0 ? Visibility.Visible : Visibility.Collapsed;
        SizeHeader.Visibility = _columns.Size > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Gives one row the current column widths: details rows have five columns, result rows four.</summary>
    private void ApplyColumns(Grid row)
    {
        var columns = row.ColumnDefinitions;
        if (columns.Count == 5)
        {
            columns[2].Width = new GridLength(_columns.Date);
            columns[3].Width = new GridLength(_columns.Type);
            columns[4].Width = new GridLength(_columns.Size);
        }
        else if (columns.Count == 4 && _tab.Search is not null)
        {
            columns[2].Width = new GridLength(_columns.Date);
            columns[3].Width = new GridLength(_columns.Size);
        }
        foreach (UIElement child in row.Children)
        {
            int column = Grid.GetColumn((FrameworkElement)child);
            if (column >= 2) child.Visibility = columns[column].Width.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void SetView(string view)
    {
        if (_tab.View == view) return;
        IReadOnlyList<string> selection = SelectedPaths.ToList();
        _tab.View = view;
        DetailsToggle.IsChecked = view != "thumbnails";
        ThumbsToggle.IsChecked = view == "thumbnails";
        BindList();
        Select(selection);
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e) => SetView("details");

    private void ThumbsToggle_Click(object sender, RoutedEventArgs e) => SetView("thumbnails");

    // ---------------------------------------------------------------- list events

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FileItem item) return;
        if (args.InRecycleQueue)
        {
            item.IsRealized = false;
            return;
        }
        item.IsRealized = true;
        if (sender == List && !PathTools.IsThisPc(_tab.Location) && args.ItemContainer.ContentTemplateRoot is Grid row) ApplyColumns(row);
        ImageCache.RequestIcon(item, PathTools.IsThisPc(_tab.Location) ? IconPixels * 3 / 2 : IconPixels, DispatcherQueue);
        if (sender == Thumbs && item.Entry.Kind == EntryKind.File) ImageCache.RequestThumbnail(item, ThumbnailPixels, DispatcherQueue);
        if (_tab.Search is not null && item.Snippet is { Length: > 0 } snippet
            && args.ItemContainer.ContentTemplateRoot is FrameworkElement root && root.FindName("SnippetText") is TextBlock block)
        {
            ShowSnippet(block, snippet);
        }
    }

    /// <summary>Shows a snippet with the matched words in bold.</summary>
    private static void ShowSnippet(TextBlock block, string snippet)
    {
        block.Inlines.Clear();
        bool marked = false;
        foreach (string part in snippet.Split([IndexStore.MarkStart, IndexStore.MarkEnd]))
        {
            if (part.Length > 0)
            {
                // Matched words are bold in the snippet's own colour, which follows the theme.
                var run = new Run { Text = part };
                if (marked) run.FontWeight = FontWeights.Bold;
                block.Inlines.Add(run);
            }
            marked = !marked;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        _owner.OnPaneSelectionChanged(this);
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) OpenItem(item);
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Native.IsKeyDown(0x10), ctrl = Native.IsKeyDown(0x11), alt = Native.IsKeyDown(0x12);
        switch (e.Key)
        {
            case VirtualKey.Enter when alt:
                ShowProperties();
                break;
            case VirtualKey.Enter:
                OpenSelected();
                break;
            case VirtualKey.Back:
                GoUp();
                break;
            case VirtualKey.Delete:
                Delete(permanently: shift);
                break;
            case VirtualKey.F2:
                BeginRename();
                break;
            case VirtualKey.Escape when _tab.Search is not null:
                CloseSearch();
                break;
            case VirtualKey.Escape when _filter.Length > 0:
                SearchBox.Text = "";
                break;
            default:
                return;
        }
        if (!ctrl) e.Handled = true;
    }

    /// <summary>Space opens the selected picture in the viewer, before the list uses Space for selection.</summary>
    private void List_PreviewSpace(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || Native.IsKeyDown(0x11) || _typed.Length > 0 && DateTime.UtcNow - _typedAt < TimeSpan.FromSeconds(1)) return;
        if (SelectedItems is [{ Entry.Kind: EntryKind.File } item] && FileTypes.IsViewerImage(item.Entry.Extension))
        {
            ShowInViewer(item);
            e.Handled = true;
        }
    }

    /// <summary>Typing letters jumps to the first name that starts with them.</summary>
    private void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        char c = args.Character;
        if (char.IsControl(c)) return;
        DateTime now = DateTime.UtcNow;
        if (now - _typedAt > TimeSpan.FromSeconds(1)) _typed = "";
        if (_typed.Length == 0 && char.IsWhiteSpace(c)) return;
        _typedAt = now;
        _typed += c;
        FileItem? match = _items.FirstOrDefault(i => i.DisplayName.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase));
        if (match is null) return;
        ListViewBase list = CurrentList;
        list.SelectedItems.Clear();
        list.SelectedItem = match;
        list.ScrollIntoView(match);
        if (list.ContainerFromItem(match) is Control container) container.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void Root_GotFocus(object sender, RoutedEventArgs e) => _owner?.SetActivePane(this);

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e) => _owner?.SetActivePane(this);

    // ---------------------------------------------------------------- opening

    internal void OpenSelected()
    {
        IReadOnlyList<FileItem> selected = SelectedItems;
        if (selected.Count == 0) return;
        if (selected.Count == 1 || selected.All(i => i.Entry.IsContainer))
        {
            OpenItem(selected[0]);
            return;
        }
        foreach (FileItem item in selected.Where(i => !i.Entry.IsContainer).Take(15)) ShellActions.Open(item.Entry.FullPath, _owner.Hwnd);
    }

    internal async void OpenItem(FileItem item)
    {
        string path = item.Entry.FullPath;
        if (item.Entry.IsContainer)
        {
            await NavigateAsync(path);
            return;
        }
        if (string.Equals(item.Entry.Extension, ".lnk", StringComparison.OrdinalIgnoreCase) && ShellActions.ShortcutFolder(path) is { } target)
        {
            await NavigateAsync(target);
            return;
        }
        if (Settings.OpenImagesInViewer && FileTypes.IsViewerImage(item.Entry.Extension))
        {
            ShowInViewer(item);
            return;
        }
        if (!ShellActions.Open(path, _owner.Hwnd)) _owner.ShowMessage(Loc.F("OpenFailed", item.Entry.Name), InfoBarSeverity.Error);
    }

    internal void ShowInViewer(FileItem item)
    {
        List<string> pictures = _items.Where(i => i.Entry.Kind == EntryKind.File && FileTypes.IsViewerImage(i.Entry.Extension)).Select(i => i.Entry.FullPath).ToList();
        int index = pictures.FindIndex(p => string.Equals(p, item.Entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _owner.ShowViewer(pictures, index, this);
    }

    /// <summary>Selects a file after the viewer moved to it, so closing the viewer leaves the list on the same picture.</summary>
    internal void Reveal(string path)
    {
        FileItem? item = _items.FirstOrDefault(i => string.Equals(i.Entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        CurrentList.SelectedItems.Clear();
        CurrentList.SelectedItem = item;
        CurrentList.ScrollIntoView(item);
    }

    internal async void OpenLocation(FileItem item)
    {
        string? folder = Path.GetDirectoryName(item.Entry.FullPath);
        if (folder is null) return;
        CloseSearchUi();
        await NavigateAsync(folder, item.Entry.FullPath);
    }

    // ---------------------------------------------------------------- file operations

    internal void Copy(bool cut)
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0) return;
        if (!FileClipboard.Put(paths, cut, _owner.Hwnd))
        {
            _owner.ShowMessage(Loc.T("ClipboardBusy"), InfoBarSeverity.Warning);
            return;
        }
        _owner.SetCutPaths(cut ? paths : []);
        _owner.ShowMessage(Loc.F(cut ? "CutDone" : "CopyDone", paths.Count), InfoBarSeverity.Informational);
    }

    internal async void Paste(string? into = null)
    {
        string? target = into ?? Folder;
        if (target is null) return;
        var content = FileClipboard.Get(_owner.Hwnd);
        if (content is not { } clip || clip.Paths.Count == 0) return;
        if (!await CheckedTransfer(clip.Paths, target, clip.Cut)) return;
        if (clip.Cut)
        {
            FileClipboard.Clear(_owner.Hwnd);
            _owner.SetCutPaths([]);
        }
    }

    internal async void TransferToOtherPane(bool move)
    {
        FilePane? other = _owner.OtherPane(this);
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0) return;
        if (other?.Folder is not { } target)
        {
            _owner.ShowMessage(Loc.T("OtherPaneNoFolder"), InfoBarSeverity.Warning);
            return;
        }
        await CheckedTransfer(paths, target, move);
    }

    /// <summary>Checks a copy or move, then hands it to Windows. Returns false when it did not start.</summary>
    private async Task<bool> CheckedTransfer(IReadOnlyList<string> paths, string target, bool move)
    {
        switch (TransferCheck.Check(paths, target, move))
        {
            case TransferProblem.IntoItself:
                _owner.ShowMessage(Loc.T("IntoItself"), InfoBarSeverity.Warning);
                return false;
            case TransferProblem.AlreadyThere:
            case TransferProblem.Nothing:
                return false;
        }
        try
        {
            if (move) await ShellActions.MoveAsync(paths, target, _owner.Hwnd);
            else await ShellActions.CopyAsync(paths, target, _owner.Hwnd);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
            return false;
        }
        _owner.RefreshPanesShowing(target);
        return true;
    }

    internal async void Delete(bool permanently)
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0) return;
        int index = CurrentList.SelectedIndex;
        try
        {
            await ShellActions.DeleteAsync(paths, _owner.Hwnd, permanently);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
            return;
        }
        await RefreshAsync();
        // The row after the deleted ones gets the selection, so Delete can be pressed again.
        if (_items.Count > 0 && CurrentList.SelectedIndex < 0) CurrentList.SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
    }

    internal void BeginRename()
    {
        if (SelectedItems is not [var item, ..] || item.Entry.Kind == EntryKind.Drive) return;
        CurrentList.SelectedItems.Clear();
        CurrentList.SelectedItem = item;
        CurrentList.ScrollIntoView(item);
        item.IsRenaming = true;
    }

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItem item) return;
        box.Text = item.Entry.Name;
        box.Focus(FocusState.Programmatic);
        int dot = item.Entry.Kind == EntryKind.File ? item.Entry.Name.LastIndexOf('.') : -1;
        if (dot > 0) box.Select(0, dot);
        else box.SelectAll();
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItem item } box) return;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRename(item, box.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            item.IsRenaming = false;
            FocusList();
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileItem item } box) CommitRename(item, box.Text);
    }

    private async void CommitRename(FileItem item, string text)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        string name = text.Trim();
        FocusList();
        if (name.Length == 0 || name == item.Entry.Name) return;
        NameProblem problem = NameRules.Check(name);
        if (problem != NameProblem.None)
        {
            _owner.ShowMessage(Loc.T("Name" + problem), InfoBarSeverity.Warning);
            return;
        }
        string? folder = Path.GetDirectoryName(item.Entry.FullPath.TrimEnd('\\'));
        if (folder is null) return;
        _selectAfterRefresh = Path.Combine(folder, name);
        try
        {
            await ShellActions.RenameAsync(item.Entry.FullPath, name, _owner.Hwnd);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        await RefreshAsync();
    }

    internal async void NewFolder()
    {
        if (Folder is not { } folder) return;
        string name = NameRules.Unused(Loc.T("NewFolderName"), isFolder: true, n => Path.Exists(Path.Combine(folder, n)));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
            return;
        }
        await SelectNewAndRename(Path.Combine(folder, name));
    }

    internal async void NewTextFile()
    {
        if (Folder is not { } folder) return;
        string name = NameRules.Unused(Loc.T("NewTextFileName") + ".txt", isFolder: false, n => Path.Exists(Path.Combine(folder, n)));
        try
        {
            using (new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
            return;
        }
        await SelectNewAndRename(Path.Combine(folder, name));
    }

    private async Task SelectNewAndRename(string path)
    {
        await RefreshAsync();
        FileItem? item = _items.FirstOrDefault(i => PathTools.SameLocation(i.Entry.FullPath, path));
        if (item is null) return;
        CurrentList.SelectedItems.Clear();
        CurrentList.SelectedItem = item;
        CurrentList.ScrollIntoView(item);
        BeginRename();
    }

    internal void CopyPaths()
    {
        IReadOnlyList<string> paths = SelectedPaths.Count > 0 ? SelectedPaths : Folder is { } folder ? new[] { folder } : Array.Empty<string>();
        if (paths.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p)));
        Clipboard.SetContent(package);
        _owner.ShowMessage(Loc.F("PathCopied", paths.Count), InfoBarSeverity.Informational);
    }

    internal void ShowProperties()
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0 && SelectedItems is [{ Entry.Kind: EntryKind.Drive } drive]) paths = new[] { drive.Entry.FullPath };
        if (paths.Count == 0 && Folder is { } folder) paths = new[] { folder };
        ShellActions.ShowProperties(paths, _owner.Hwnd);
    }

    internal void SelectAll() => CurrentList.SelectAll();

    internal void OpenTerminal()
    {
        string? folder = FolderOrSearchRoot;
        if (folder is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
    }

    // ---------------------------------------------------------------- context menus

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        Windows.Foundation.Point point = e.GetPosition(ContentArea);
        if (element?.DataContext is FileItem item)
        {
            ListViewBase list = CurrentList;
            if (!list.SelectedItems.Contains(item))
            {
                list.SelectedItems.Clear();
                list.SelectedItem = item;
            }
            if (Native.IsKeyDown(0x10))
            {
                // Shift+right-click still shows Windows' own menu, as a fallback.
                ShowNativeMenu();
            }
            else
            {
                ShowItemMenu(item, point);
            }
        }
        else
        {
            CurrentList.SelectedItems.Clear();
            ShowBackgroundMenu(point);
        }
        e.Handled = true;
    }

    /// <summary>The app's menu for the selection, completed with the commands of the Windows menu.</summary>
    private void ShowItemMenu(FileItem item, Windows.Foundation.Point point)
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0 && item.Entry.Kind == EntryKind.Drive) paths = new[] { item.Entry.FullPath };
        var loading = paths.Count > 0
            ? ShellMenuSession.ForItemsAsync(paths, _owner.Hwnd, extended: false)
            : Task.FromResult<(ShellMenuSession, List<ShellMenuEntry>)?>(null);
        ShellMenuMerge.Show(BuildItemMenu(item), ContentArea, point, loading, _owner.Hwnd, background: false);
    }

    /// <summary>The app's menu for the empty area, with Windows' full "New" submenu when the folder has one.</summary>
    private void ShowBackgroundMenu(Windows.Foundation.Point point)
    {
        var loading = Folder is { } folder
            ? ShellMenuSession.ForBackgroundAsync(folder, _owner.Hwnd)
            : Task.FromResult<(ShellMenuSession, List<ShellMenuEntry>)?>(null);
        ShellMenuMerge.Show(BuildBackgroundMenu(), ContentArea, point, loading, _owner.Hwnd, background: true);
    }

    private static MenuFlyoutItemBase Tagged(MenuFlyoutItemBase item, string tag)
    {
        item.Tag = tag;
        return item;
    }

    private void Content_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled) return;
        ShowBackgroundMenu(e.GetPosition(ContentArea));
        e.Handled = true;
    }

    internal void ShowNativeMenu()
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0 && SelectedItems is [{ Entry.Kind: EntryKind.Drive } drive]) paths = new[] { drive.Entry.FullPath };
        if (paths.Count == 0) return;
        Native.GetCursorPos(out Native.POINT point);
        ShellContextMenu.Show(_owner.Hwnd, paths, point, BeginRename);
    }

    private static MenuFlyoutItem Item(string key, string glyph, Action action, string? accelerator = null, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = Loc.T(key), Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
        item.Click += (_, _) => action();
        return item;
    }

    private MenuFlyout BuildItemMenu(FileItem item)
    {
        var menu = new MenuFlyout();
        bool container = item.Entry.IsContainer;
        bool drive = item.Entry.Kind == EntryKind.Drive;
        bool picture = !container && FileTypes.IsViewerImage(item.Entry.Extension);
        bool several = SelectedItems.Count > 1;
        var open = Item("Open", "", OpenSelected, "Enter");
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        if (picture) menu.Items.Add(Item("ViewPicture", "", () => ShowInViewer(item), "Space"));
        if (container && !several)
        {
            menu.Items.Add(Item("OpenInNewTab", "", () => OpenTab(item.Entry.FullPath, select: false)));
            menu.Items.Add(Item("OpenInOtherPane", "", () => _owner.OpenInOtherPane(this, item.Entry.FullPath)));
        }
        // Windows' menu brings its own "Open with" and replaces this one when it arrives; this one serves while Windows'
        // menu is still loading or when it fails. It opens one file, so it is offered only when one file is selected.
        if (!container && !several) menu.Items.Add(Tagged(Item("OpenWith", "", () => ShellActions.OpenWith(item.Entry.FullPath, _owner.Hwnd)), ShellMenuMerge.OpenWithTag));
        if (_tab.Search is not null) menu.Items.Add(Item("OpenLocation","", () => OpenLocation(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!drive)
        {
            menu.Items.Add(Item("Cut", "", () => Copy(cut: true), "Ctrl+X"));
            menu.Items.Add(Item("Copy", "", () => Copy(cut: false), "Ctrl+C"));
        }
        if (container && !several) menu.Items.Add(Item("PasteInto", "", () => Paste(item.Entry.FullPath), enabled: FileClipboard.HasFiles));
        menu.Items.Add(Item("CopyPath", "", CopyPaths, "Ctrl+Shift+C"));
        if (!drive)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            bool otherFolder = _owner.OtherPane(this)?.Folder is not null;
            menu.Items.Add(Item("CopyToOther", "", () => TransferToOtherPane(move: false), "F7", otherFolder));
            menu.Items.Add(Item("MoveToOther", "", () => TransferToOtherPane(move: true), "F8", otherFolder));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Rename", "", BeginRename, "F2", !several));
            menu.Items.Add(Item("Delete", "", () => Delete(permanently: false), "Del"));
        }
        if (container && !several)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("PinToSidebar", "", () => _owner.Pin(item.Entry.FullPath)));
            if (Settings.AiSearch && App.Instance!.Search.CoverageOf(item.Entry.FullPath) != Coverage.Full)
                menu.Items.Add(Item("AddToAiIndex", "", () => _owner.AddToIndex(item.Entry.FullPath)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Tagged(Item("Properties", "", ShowProperties, "Alt+Enter"), ShellMenuMerge.PropertiesTag));
        return menu;
    }

    private MenuFlyout BuildBackgroundMenu()
    {
        var menu = new MenuFlyout();
        bool folder = Folder is not null;
        if (folder)
        {
            var create = new MenuFlyoutSubItem { Text = Loc.T("New"), Icon = new FontIcon { Glyph = "" }, Tag = ShellMenuMerge.NewTag };
            create.Items.Add(Item("NewFolder", "", NewFolder, "Ctrl+Shift+N"));
            create.Items.Add(Item("NewTextFile", "", NewTextFile));
            menu.Items.Add(create);
            menu.Items.Add(Item("Paste", "", () => Paste(), "Ctrl+V", FileClipboard.HasFiles));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (_tab.Search is null)
        {
            var sort = new MenuFlyoutSubItem { Text = Loc.T("SortBy"), Icon = new FontIcon { Glyph = "" } };
            foreach (SortColumn column in Enum.GetValues<SortColumn>())
            {
                var option = new RadioMenuFlyoutItem { Text = Loc.T("Column" + column), GroupName = "sort", IsChecked = _tab.Sort == column };
                option.Click += (_, _) => SetSort(column, _tab.Descending);
                sort.Items.Add(option);
            }
            sort.Items.Add(new MenuFlyoutSeparator());
            var descending = new ToggleMenuFlyoutItem { Text = Loc.T("Descending"), IsChecked = _tab.Descending };
            descending.Click += (_, _) => SetSort(_tab.Sort, !_tab.Descending);
            sort.Items.Add(descending);
            menu.Items.Add(sort);
            var view = new MenuFlyoutSubItem { Text = Loc.T("View"), Icon = new FontIcon { Glyph = "" } };
            var details = new RadioMenuFlyoutItem { Text = Loc.T("ViewDetails"), GroupName = "view", IsChecked = _tab.View != "thumbnails" };
            details.Click += (_, _) => SetView("details");
            var thumbs = new RadioMenuFlyoutItem { Text = Loc.T("ViewThumbnails"), GroupName = "view", IsChecked = _tab.View == "thumbnails" };
            thumbs.Click += (_, _) => SetView("thumbnails");
            view.Items.Add(details);
            view.Items.Add(thumbs);
            menu.Items.Add(view);
        }
        menu.Items.Add(Item("Refresh", "", async () => await ReloadAsync(), "F5"));
        if (FolderOrSearchRoot is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("OpenTerminal", "", OpenTerminal));
            menu.Items.Add(Item("CopyPath", "", CopyPaths));
            menu.Items.Add(Item("PinToSidebar", "", () => _owner.Pin(FolderOrSearchRoot!)));
            menu.Items.Add(Tagged(Item("Properties", "", ShowProperties), ShellMenuMerge.PropertiesTag));
        }
        return menu;
    }

    // ---------------------------------------------------------------- drag and drop

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        string[] paths = e.Items.OfType<FileItem>().Where(i => i.Entry.Kind != EntryKind.Drive).Select(i => i.Entry.FullPath).ToArray();
        if (paths.Length == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.Properties.Add(InternalDragFormat, paths);
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        // Other programs, such as Explorer or a mail client, receive the files as storage items.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            DataProviderDeferral deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (string path in paths)
                {
                    items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                }
                request.SetData(items);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => ScheduleRefresh();

    private string? DropTarget(DragEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext is FileItem { Entry.IsContainer: true } item ? item.Entry.FullPath : Folder;

    private void Content_DragOver(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        bool files = e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Properties.ContainsKey(InternalDragFormat);
        if (target is null || !files)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        string[]? internalPaths = e.DataView.Properties.TryGetValue(InternalDragFormat, out object? value) ? value as string[] : null;
        if (internalPaths is not null && internalPaths.Any(p => PathTools.IsSameOrInside(target, p)))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        bool move = DropMoves(e, internalPaths, target);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.F(move ? "DropMove" : "DropCopy", PlaceName(target));
        e.DragUIOverride.IsCaptionVisible = true;
    }

    /// <summary>Explorer's rule: Shift moves, Ctrl copies, otherwise items on the same drive move and others copy.</summary>
    private static bool DropMoves(DragEventArgs e, string[]? sources, string target)
    {
        if (e.Modifiers.HasFlag(DragDropModifiers.Shift)) return true;
        if (e.Modifiers.HasFlag(DragDropModifiers.Control)) return false;
        if (sources is not { Length: > 0 }) return false;
        return string.Equals(Path.GetPathRoot(sources[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
    }

    private async void Content_Drop(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        if (target is null) return;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            string[]? internalPaths = e.DataView.Properties.TryGetValue(InternalDragFormat, out object? value) ? value as string[] : null;
            List<string> paths = internalPaths?.ToList() ?? [];
            if (paths.Count == 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            bool move = DropMoves(e, internalPaths ?? [.. paths], target);
            if (paths.Count > 0) await CheckedTransfer(paths, target, move);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------------------------------------------------------------- address bar

    private async void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb) await NavigateAsync(crumb.Location, ChildOf(_tab.Location, crumb.Location));
    }

    private void AddressBack_Tapped(object sender, TappedRoutedEventArgs e) => EnterAddressEdit();

    private void EnterAddressEdit()
    {
        BreadcrumbScroller.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = PathTools.IsThisPc(_tab.Location) ? "" : _tab.Location;
        // The box was collapsed, so its inner text field exists only after a layout pass. The keyboard focus goes
        // straight into that field; focusing the AutoSuggestBox itself left typing with nowhere to go.
        AddressBox.UpdateLayout();
        DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (AddressBox.FindDescendant<TextBox>() is not { } box)
            {
                AddressBox.Focus(FocusState.Keyboard);
                return;
            }
            box.Focus(FocusState.Keyboard);
            box.SelectAll();
        });
    }

    private void ExitAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbScroller.Visibility = Visibility.Visible;
    }

    private async void AddressBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        ExitAddressEdit();
        if (text.Length == 0 || string.Equals(text, Loc.T("ThisPC"), StringComparison.CurrentCultureIgnoreCase))
        {
            await NavigateAsync(PathTools.ThisPc);
            return;
        }
        string? full = PathTools.Normalize(text, Folder);
        if (full is not null && Directory.Exists(full)) await NavigateAsync(full);
        else if (full is not null && File.Exists(full)) await NavigateAsync(Path.GetDirectoryName(full)!, full);
        else _owner.ShowMessage(Loc.F("FolderNotFound", text), InfoBarSeverity.Warning);
    }

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        ExitAddressEdit();
        FocusList();
    }

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Leaving the box for its own suggestion list keeps it open; leaving it for anything else closes it.
        DispatcherQueue.SafeEnqueue(() =>
        {
            if (XamlRoot is null) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == AddressBox) return;
            }
            ExitAddressEdit();
        });
    }

    private async void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        string text = sender.Text;
        List<string> suggestions = await Task.Run(() => FolderSuggestions(text));
        if (sender.Text == text) sender.ItemsSource = suggestions;
    }

    private static List<string> FolderSuggestions(string text)
    {
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(text.Trim('"'));
            if (expanded.Length < 2 || !Path.IsPathFullyQualified(expanded)) return [];
            string parent = expanded.EndsWith('\\') ? expanded : Path.GetDirectoryName(expanded) ?? "";
            string prefix = expanded.EndsWith('\\') ? "" : Path.GetFileName(expanded);
            if (!Directory.Exists(parent)) return [];
            return new DirectoryInfo(parent).EnumerateDirectories(prefix + "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Take(15).Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- filter and search

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (Settings.AiSearch) App.Instance!.Search.Warm();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_switchingTabs || _tab.Search is not null) return;
        _filter = SearchBox.Text.Trim();
        ApplyFilter(SelectedPaths.ToList());
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await StartSearchAsync();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_tab.Search is not null)
                {
                    CloseSearch();
                }
                else
                {
                    SearchBox.Text = "";
                    FocusList();
                }
                break;
            case VirtualKey.Down:
                e.Handled = true;
                FocusList();
                break;
        }
    }

    private async Task StartSearchAsync()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        bool firstSearch = _tab.Search is null;
        SearchState state = _tab.Search ?? new SearchState();
        state.Query = query;
        if (firstSearch)
        {
            state.Smart = App.Instance!.Search.IsRunning && App.Instance.Search.CoverageOf(_tab.Location) != Coverage.None;
            _tab.Selection = SelectedPaths.Take(500).ToList();
        }
        _tab.Search = state;
        _filter = "";
        OpenSearchUi(run: false);
        await RunSearchAsync();
    }

    private void OpenSearchUi(bool run)
    {
        SearchState state = _tab.Search!;
        StopWatcher();
        SearchBar.Visibility = Visibility.Visible;
        bool aiRunning = App.Instance!.Search.IsRunning;
        SmartToggle.IsEnabled = aiRunning;
        ToolTipService.SetToolTip(SmartToggle, aiRunning ? Loc.T("SmartSearchTip") : Loc.T("SmartSearchOffTip"));
        SmartToggle.IsChecked = state.Smart;
        ExactToggle.IsChecked = !state.Smart;
        ExactOptions.Visibility = state.Smart ? Visibility.Collapsed : Visibility.Visible;
        NamesCheck.IsChecked = state.InNames;
        ContentsCheck.IsChecked = state.InContents;
        SubfoldersCheck.IsChecked = state.Subfolders;
        UpdateChrome();
        if (run) _ = RunSearchAsync();
    }

    private void CloseSearchUi()
    {
        CancelSearch();
        _tab.Search = null;
        SearchBar.Visibility = Visibility.Collapsed;
        AddToIndexButton.Visibility = Visibility.Collapsed;
        EngineBadge.Visibility = Visibility.Collapsed;
        _switchingTabs = true;
        SearchBox.Text = "";
        _switchingTabs = false;
    }

    /// <summary>Closes the search and shows the folder again, with the keyboard focus in the list once it is filled.</summary>
    internal async void CloseSearch()
    {
        if (_tab.Search is null) return;
        CloseSearchUi();
        await LoadAsync();
        FocusListSoon();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void StopSearch_Click(object sender, RoutedEventArgs e) => CancelSearch();

    private async void SmartToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_tab.Search is null) return;
        _tab.Search.Smart = true;
        OpenSearchUi(run: false);
        await RunSearchAsync();
    }

    private async void ExactToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_tab.Search is null) return;
        _tab.Search.Smart = false;
        OpenSearchUi(run: false);
        await RunSearchAsync();
    }

    private async void SearchOption_Click(object sender, RoutedEventArgs e)
    {
        if (_tab.Search is not { } state) return;
        state.InNames = NamesCheck.IsChecked == true;
        state.InContents = ContentsCheck.IsChecked == true;
        state.Subfolders = SubfoldersCheck.IsChecked == true;
        if (!state.InNames && !state.InContents)
        {
            state.InNames = true;
            NamesCheck.IsChecked = true;
        }
        await RunSearchAsync();
    }

    private void AddToIndex_Click(object sender, RoutedEventArgs e)
    {
        if (FolderOrSearchRoot is { } folder) _owner.AddToIndex(folder);
        AddToIndexButton.Visibility = Visibility.Collapsed;
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _flushTimer.Stop();
        SearchRing.IsActive = false;
        StopSearchButton.Visibility = Visibility.Collapsed;
    }

    private async Task RunSearchAsync()
    {
        if (_tab.Search is not { } state) return;
        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        PaneTab tab = _tab;
        _all = [];
        _items = [];
        BindList();
        UpdateHeaders();
        EmptyPanel.Visibility = Visibility.Collapsed;
        SearchRing.IsActive = true;
        StopSearchButton.Visibility = Visibility.Visible;
        SearchStatus.Text = Loc.T("Searching");
        UpdateStatus();
        _searchWatch.Restart();
        string root = PathTools.IsThisPc(tab.Location) ? "" : tab.Location;
        Coverage coverage = App.Instance!.Search.CoverageOf(tab.Location);
        AddToIndexButton.Visibility = Settings.AiSearch && coverage == Coverage.None && root.Length > 0 && !state.Smart ? Visibility.Visible : Visibility.Collapsed;
        AddToIndexButton.Content = Loc.T("AddFolderToAiIndex");
        EngineBadge.Visibility = Visibility.Visible;
        EngineText.Text = state.Smart ? Loc.T("EngineSmart") : Loc.T("EngineLive");
        if (state.Smart) await RunSmartSearchAsync(state, tab, coverage, cts);
        else await RunLiveSearchAsync(state, tab, root, cts);
    }

    private async Task RunSmartSearchAsync(SearchState state, PaneTab tab, Coverage coverage, CancellationTokenSource cts)
    {
        SearchService service = App.Instance!.Search;
        try
        {
            if (!service.ModelLoaded)
            {
                // Names and exact words need no model: they are shown at once, while the model loads for the meaning.
                List<SmartHit> quick = await service.SearchAsync(state.Query, tab.Location, cts.Token, meaning: false);
                if (cts.IsCancellationRequested || tab != _tab) return;
                await ShowSmartHitsAsync(quick, cts);
                SearchStatus.Text = Loc.T("LoadingModel");
            }
            List<SmartHit> hits = await service.SearchAsync(state.Query, tab.Location, cts.Token);
            if (cts.IsCancellationRequested || tab != _tab) return;
            await ShowSmartHitsAsync(hits, cts);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex);
            if (tab != _tab) return;
            FinishSearch(cts, Loc.F("SearchFailed", ex.Message));
            return;
        }
        if (cts.IsCancellationRequested || tab != _tab) return;
        FinishSearch(cts, coverage == Coverage.Partial ? Loc.T("PartialCoverage") : null);
    }

    private async Task ShowSmartHitsAsync(List<SmartHit> hits, CancellationTokenSource cts)
    {
        // The index can lag behind the disk for a moment; files that are gone are left out.
        // The folder column shows the names Windows shows, such as C:\Benutzer\Anna\Bilder; they are asked here, off the UI thread.
        List<(SmartHit Hit, FileEntry Entry, string Folder)> found = await Task.Run(
            () => hits.Select(h => (h, DirectoryReader.ReadOne(h.Path)))
                .Where(p => p.Item2 is not null)
                .Select(p => (p.h, p.Item2!, ShownFolder(p.Item2!.FullPath, ask: true)))
                .ToList(),
            cts.Token);
        if (cts.IsCancellationRequested) return;
        ItemStyle style = RowStyle;
        _all = found.Select(f => new FileItem(f.Entry, style)
        {
            Snippet = f.Hit.Snippet,
            Match = f.Hit.Kinds,
            FolderText = f.Folder,
        }).ToList();
        _items = new ObservableCollection<FileItem>(_all);
        BindList();
        if (_items.Count > 0) EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    private async Task RunLiveSearchAsync(SearchState state, PaneTab tab, string root, CancellationTokenSource cts)
    {
        _pendingHits.Clear();
        var counters = new SearchCounters();
        _counters = counters;
        _flushTimer.Start();
        // On "This PC" the exact search looks through every ready local drive.
        List<string> roots = root.Length > 0 ? [root] : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        try
        {
            foreach (string folder in roots)
            {
                var request = new SearchRequest(folder, state.Query, state.InNames, state.InContents, state.Subfolders, Settings.ShowHidden);
                // The names Windows shows for the hit's folder are asked here, on the search thread, so the list shows them at once.
                await LiveSearch.RunAsync(request, hit =>
                {
                    _ = ShownFolder(hit.Entry.FullPath, ask: true);
                    _pendingHits.Enqueue(hit);
                }, counters, cts.Token);
                if (cts.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        if (tab != _tab || _searchCts != cts) return;
        FlushHits();
        FinishSearch(cts, null);
    }

    /// <summary>Moves found items from the search threads into the list, a batch at a time.</summary>
    private void FlushHits()
    {
        ItemStyle style = RowStyle;
        int added = 0;
        while (added < 3000 && _pendingHits.TryDequeue(out SearchHit? hit))
        {
            var item = new FileItem(hit.Entry, style)
            {
                Snippet = hit.Snippet,
                Match = (hit.NameMatched ? MatchKind.Name : MatchKind.None) | (hit.ContentMatched ? MatchKind.Words : MatchKind.None),
                FolderText = ShownFolder(hit.Entry.FullPath, ask: false),
            };
            _all.Add(item);
            _items.Add(item);
            added++;
        }
        if (_counters is { } counters && _searchCts is not null)
        {
            SearchStatus.Text = Loc.F("SearchProgress", counters.Files.ToString("N0"), counters.Hits.ToString("N0"));
        }
        if (_items.Count > 0) EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    private void FinishSearch(CancellationTokenSource cts, string? note)
    {
        bool stopped = cts.IsCancellationRequested;
        if (_searchCts == cts) _searchCts = null;
        _flushTimer.Stop();
        SearchRing.IsActive = false;
        StopSearchButton.Visibility = Visibility.Collapsed;
        string time = (_searchWatch.Elapsed.TotalSeconds < 10 ? _searchWatch.Elapsed.TotalSeconds.ToString("0.00") : _searchWatch.Elapsed.TotalSeconds.ToString("0")) + " s";
        string text = Loc.F(stopped ? "SearchStopped" : "SearchDone", _items.Count.ToString("N0"), time);
        if (_items.Count >= LiveSearch.MaxHits) text += " " + Loc.T("SearchLimit");
        if (note is not null) text += "   ·   " + note;
        SearchStatus.Text = text;
        UpdateEmpty();
        UpdateStatus();
    }
}

internal static class TreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (child.FindDescendant<T>() is { } deeper) return deeper;
        }
        return null;
    }
}
