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
    private bool _closed;
    private readonly Dictionary<ListViewBase, HashSet<SelectorItem>> _realizedRows = [];

    /// <summary>Closed windows release their watchers and searches while the app continues in the background.</summary>
    internal void StopBackgroundWork()
    {
        _closed = true;
        _loadCts?.Cancel();
        CancelSearch();
        StopWatcher();
        EndMarquee();
    }

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
        foreach (ListViewBase list in new ListViewBase[] { List, Thumbs })
        {
            list.AddHandler(PointerPressedEvent, new PointerEventHandler(List_PointerPressedAny), handledEventsToo: true);
            list.AddHandler(PointerPressedEvent, new PointerEventHandler(Marquee_PointerPressed), handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, new PointerEventHandler(Marquee_PointerMoved), handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, new PointerEventHandler(Marquee_PointerReleased), handledEventsToo: true);
        }
    }

    // ---------------------------------------------------------------- public surface

    internal PaneTab Tab => _tab;

    internal string Location => _tab.Location;

    /// <summary>The folder shown, or null on "This PC" and while search results are shown.</summary>
    internal string? Folder => _tab.Search is null && !PathTools.IsThisPc(_tab.Location) && !PathTools.IsShellPlace(_tab.Location) ? _tab.Location : null;

    /// <summary>The folder the pane is in, also while search results are shown.</summary>
    internal string? FolderOrSearchRoot => PathTools.IsThisPc(_tab.Location) || PathTools.IsShellPlace(_tab.Location) ? null : _tab.Location;

    internal bool IsSearchOpen => _tab.Search is not null;

    /// <summary>The place described by the preview, including virtual folders which cannot receive file operations.</summary>
    internal string? PreviewLocation => _tab.Search is null ? _tab.Location : FolderOrSearchRoot;

    internal IReadOnlyList<FileItem> SelectedItems => CurrentList.SelectedItems.OfType<FileItem>().ToList();

    internal IReadOnlyList<string> SelectedPaths => SelectedItems.Where(i => i.Entry.Kind != EntryKind.Drive).Select(i => i.Entry.FullPath).ToList();

    internal IReadOnlyList<FileItem> VisibleItems => _items;

    private ListViewBase CurrentList => _tab.View == "thumbnails" && !PathTools.IsThisPc(_tab.Location) && _tab.Search is null ? Thumbs : List;

    private AppSettings Settings => App.Instance!.Settings;

    private ListingOptions Listing => new(Settings.ShowHidden, Settings.ShowProtectedFiles);

    private ItemStyle RowStyle => new(_owner.IsDark, Settings.ColorfulNames, Settings.ShowExtensions);

    private int IconPixels => (int)Math.Ceiling(20 * (XamlRoot?.RasterizationScale ?? 1));

    private int ThumbnailPixels => Math.Min(256, (int)Math.Ceiling(128 * (XamlRoot?.RasterizationScale ?? 1)));

    internal void RefreshImagesForDpi()
    {
        if (_closed || _tab is null) return;
        foreach (FileItem item in _all.Where(item => item.IsRealized))
        {
            ImageCache.RequestIcon(item, PathTools.IsThisPc(_tab.Location) ? IconPixels * 3 / 2 : IconPixels, DispatcherQueue);
            if (CurrentList == Thumbs && item.Entry.Kind == EntryKind.File) ImageCache.RequestThumbnail(item, ThumbnailPixels, DispatcherQueue);
        }
    }

    internal void Initialize(MainWindow owner, PaneSettings settings, string? start, string fallback)
    {
        _owner = owner;
        foreach (string location in settings.Tabs)
        {
            if (PathTools.IsThisPc(location) || PathTools.IsShellPlace(location) || Directory.Exists(location)) AddTab(location, settings, select: false);
        }
        if (start is not null)
        {
            string folder = PathTools.IsThisPc(start) || PathTools.IsShellPlace(start) || Directory.Exists(start) ? start : Path.GetDirectoryName(start) ?? fallback;
            AddTab(folder, settings, select: false).Selection = !PathTools.IsShellPlace(start) && File.Exists(start) ? [start] : [];
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
        if (!PathTools.IsThisPc(location) && !PathTools.IsShellPlace(location) && File.Exists(location))
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
        if (!PathTools.IsThisPc(location) && !PathTools.IsShellPlace(location) && !Directory.Exists(location))
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
        if (PathTools.IsShellPlace(from))
        {
            // Windows named the place one level up when the shell place was listed.
            await NavigateAsync(_tab.ShellParent ?? PathTools.ThisPc, from);
            return;
        }
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
    internal static string PlaceName(string location) =>
        PathTools.IsThisPc(location) ? Loc.T("ThisPC")
        : PathTools.IsShellPlace(location) ? ShellFolderReader.KnownName(location) ?? ShellPlaceFallbackName(location)
        : ShellNames.Known(location);

    /// <summary>A readable name for a shell place whose own name Windows has not given yet.</summary>
    private static string ShellPlaceFallbackName(string location)
    {
        string parsing = PathTools.ParsingName(location).TrimEnd('\\');
        int slash = parsing.LastIndexOf('\\');
        return slash >= 0 && slash < parsing.Length - 1 ? parsing[(slash + 1)..] : parsing;
    }

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
        if (PathTools.IsShellPlace(location))
        {
            // A shell place shows the place above it, when Windows named one, and itself.
            var crumbs = new List<Crumb> { new(Loc.T("ThisPC"), PathTools.ThisPc) };
            if (_tab.ShellParent is { } above && !PathTools.IsThisPc(above)) crumbs.Add(new Crumb(PlaceName(above), above));
            crumbs.Add(new Crumb(PlaceName(location), location));
            Breadcrumb.ItemsSource = crumbs;
            return;
        }
        List<Core.PathSegment> segments = PathTools.Segments(location, Loc.T("ThisPC"));
        Breadcrumb.ItemsSource = segments.Select(s => new Crumb(PlaceName(s.Location), s.Location)).ToList();
        Microsoft.UI.Dispatching.DispatcherQueue queue = DispatcherQueue;
        Task.Run(() =>
        {
            // The folder names are shown as soon as they are known, so a drive that answers slowly never holds them
            // back. Then comes the drive's own name: a drive in this PC is asked every time, a network drive only until
            // its name is known, and the bar is drawn once more when it arrives.
            if (segments.Count > 2)
            {
                foreach (Core.PathSegment segment in segments.Skip(2)) ShellNames.OfFolder(segment.Location);
                queue.SafeEnqueue(() => RedrawCrumbs(location, segments));
            }
            if (segments.Count > 1) ShellNames.OfDrive(segments[1].Location);
        }).ContinueWith(_ => queue.SafeEnqueue(() => RedrawCrumbs(location, segments)), TaskScheduler.Default);
    }

    /// <summary>Draws the address bar, tab header and title again with the names known now, if the pane is still there.</summary>
    private void RedrawCrumbs(string location, List<Core.PathSegment> segments)
    {
        if (!PathTools.SameLocation(location, _tab.Location)) return;
        Breadcrumb.ItemsSource = segments.Select(s => new Crumb(PlaceName(s.Location), s.Location)).ToList();
        UpdateTabHeader();
        _owner.OnPaneLocationChanged(this);
        DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => BreadcrumbScroller.ChangeView(BreadcrumbScroller.ScrollableWidth, null, null, true));
    }

    private void UpdateChrome()
    {
        BackButton.IsEnabled = _tab.History.CanGoBack || _tab.Search is not null;
        ForwardButton.IsEnabled = _tab.History.CanGoForward;
        UpButton.IsEnabled = PathTools.Parent(_tab.Location) is not null || _tab.Search is not null;
        if (!PathTools.IsShellPlace(_tab.Location)) _tab.ShellParent = null;
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
        if (_closed) return;
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
            entries = await ReadEntriesAsync(tab, location, listing, cts.Token);
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
        _owner.OnPaneContentsChanged();
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

    /// <summary>
    /// Reads a place: the drives for This PC, Windows' own list for a shell place, the folder otherwise. For a shell
    /// place the tab learns the place one level up, and the address bar is drawn again with the names Windows gave.
    /// </summary>
    private async Task<List<FileEntry>> ReadEntriesAsync(PaneTab tab, string location, ListingOptions listing, CancellationToken token)
    {
        if (PathTools.IsShellPlace(location))
        {
            ShellListing shell = await ShellFolderReader.ReadAsync(location, listing.ShowHidden, token);
            token.ThrowIfCancellationRequested();
            tab.ShellParent = shell.Parent;
            if (tab == _tab && ReferenceEquals(location, tab.Location))
            {
                ShowCrumbs();
                UpdateTabHeader();
                _owner.OnPaneLocationChanged(this);
            }
            return shell.Entries;
        }
        return await Task.Run(() => PathTools.IsThisPc(location) ? DirectoryReader.ReadDrives() : DirectoryReader.Read(location, listing, token), token);
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
        _items = new ObservableCollection<FileItem>(SearchOrder(visible));
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

    private void Select(IReadOnlyList<string> paths, bool scroll = true)
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
        if (scroll && first is not null) list.ScrollIntoView(first, ScrollIntoViewAlignment.Default);
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
        if (PathTools.IsThisPc(location) || PathTools.IsShellPlace(location)) return;
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
        if (_closed || PathTools.IsThisPc(location) || PathTools.IsShellPlace(location)) return;
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
        if (_closed) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Reads the folder again and updates only the rows that changed, keeping the selection and scroll position.</summary>
    private async Task RefreshAsync()
    {
        if (_closed || _tab.Search is not null) return;
        PaneTab tab = _tab;
        string location = tab.Location;
        ListingOptions listing = Listing;
        List<FileEntry> fresh;
        try
        {
            fresh = await ReadEntriesAsync(tab, location, listing, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_closed) return;
            // The folder itself was deleted or renamed: go to the nearest folder that still exists.
            // A shell place that cannot be listed any more keeps its rows until the user goes elsewhere.
            if (PathTools.IsShellPlace(location)) return;
            string? up = location;
            while (up is not null && !PathTools.IsThisPc(up) && !Directory.Exists(up)) up = PathTools.Parent(up);
            if (tab == _tab && up is not null && !PathTools.SameLocation(up, location)) await NavigateAsync(up);
            return;
        }
        if (_closed || tab != _tab || !PathTools.SameLocation(location, tab.Location) || tab.Search is not null) return;
        FolderChanges changes = FolderChanges.Compute(_entries, fresh);
        _entries = fresh;
        if (!changes.IsEmpty)
        {
            _owner.OnPaneContentsChanged();
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
        (SortColumn? current, bool descending) = CurrentSort;
        SetSort(column, column == current ? !descending : column is SortColumn.Modified or SortColumn.Size);
    }

    /// <summary>The order the list shows: the folder's own, or the search's, where null means the order found.</summary>
    private (SortColumn? Column, bool Descending) CurrentSort =>
        _tab.Search is { } search ? (search.Sort, search.Descending) : (_tab.Sort, _tab.Descending);

    internal void SetSort(SortColumn column, bool descending)
    {
        if (_tab.Search is { } search)
        {
            // Search results have their own order, so the folder keeps its sort when the search is closed.
            search.Sort = column;
            search.Descending = descending;
            UpdateHeaders();
            ShowSearchOrder();
            return;
        }
        _tab.Sort = column;
        _tab.Descending = descending;
        UpdateHeaders();
        IReadOnlyList<string> selection = SelectedPaths.ToList();
        ShowEntries(selection);
    }

    /// <summary>Puts search results back in the order they were found: by relevance for the smart search.</summary>
    private void SetFoundOrder()
    {
        if (_tab.Search is not { } search) return;
        search.Sort = null;
        UpdateHeaders();
        ShowSearchOrder();
    }

    /// <summary>
    /// Shows the search results again in the current order. With <paramref name="keepPlace"/>, used when the last hits
    /// of a search come in all at once, the list stays where the user had scrolled and keeps the keyboard focus.
    /// </summary>
    private void ShowSearchOrder(bool keepPlace = false)
    {
        IReadOnlyList<string> selection = SelectedPaths.ToList();
        ListViewBase list = CurrentList;
        ScrollViewer? viewer = keepPlace ? list.FindDescendant<ScrollViewer>() : null;
        double offset = viewer?.VerticalOffset ?? 0;
        bool focused = keepPlace && IsInside(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, list);
        _items = new ObservableCollection<FileItem>(SearchOrder(_all));
        BindList();
        if (selection.Count > 0) Select(selection, scroll: !keepPlace);
        if (viewer is not null) DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => viewer.ChangeView(null, offset, null, true));
        if (focused) FocusListSoon();
        UpdateStatus();
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container)) return true;
        }
        return false;
    }

    /// <summary>Search results in the chosen column's order, or as found while no column is chosen.</summary>
    private IEnumerable<FileItem> SearchOrder(IEnumerable<FileItem> items) =>
        _tab.Search is { } search ? EntrySorter.Order(items, i => i.Entry, search.Sort, search.Descending) : items;

    private void UpdateHeaders()
    {
        (SortColumn? sorted, bool descending) = CurrentSort;
        string Mark(SortColumn column) => column == sorted ? (descending ? "  ↓" : "  ↑") : "";
        NameHeader.Content = Loc.T("ColumnName") + Mark(SortColumn.Name);
        DateHeader.Content = Loc.T("ColumnModified") + Mark(SortColumn.Modified);
        TypeHeader.Content = Loc.T("ColumnType") + Mark(SortColumn.Type);
        SizeHeader.Content = Loc.T("ColumnSize") + Mark(SortColumn.Size);
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
        if (!_realizedRows.TryGetValue(sender, out var rows)) _realizedRows[sender] = rows = [];
        if (args.InRecycleQueue)
        {
            rows.Remove(args.ItemContainer);
            if (args.Item is FileItem recycled) recycled.IsRealized = false;
            return;
        }
        if (args.Item is not FileItem item) return;
        rows.Add(args.ItemContainer);
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
        // Correcting a click or updating a selection box can raise this once per row.
        // Recalculate the status and preview once after that batch, not for every item.
        if (_adjustingSelection) return;
        if (sender is ListViewBase list) KeepClickSelection(list, e);
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        UpdateStatus();
        _owner.OnPaneSelectionChanged(this);
    }

    /// <summary>When the last mouse press on the list happened, so a selection change can be told to come from a click.</summary>
    private long _listPressedAt;

    /// <summary>The row a Shift+click selects from: the row of the last click without Shift.</summary>
    private FileItem? _selectionAnchor;

    private bool _adjustingSelection;

    private void List_PointerPressedAny(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) _listPressedAt = Environment.TickCount64;
    }

    /// <summary>
    /// Ctrl+click adds a row to the selection and Shift+click selects the rows from the last clicked one, as in Explorer.
    /// The list does this itself only when it sees the modifier keys, which it does not always do; when a click with
    /// Ctrl or Shift held replaced the selection instead, the selection is put right here.
    /// </summary>
    private void KeepClickSelection(ListViewBase list, SelectionChangedEventArgs e)
    {
        if (_adjustingSelection || Environment.TickCount64 - _listPressedAt > 1500) return;
        bool ctrl = Native.IsKeyDown(0x11), shift = Native.IsKeyDown(0x10);
        FileItem? clicked = e.AddedItems.Count == 1 ? e.AddedItems[0] as FileItem : null;
        _adjustingSelection = true;
        try
        {
            if (shift && clicked is not null && _selectionAnchor is { } anchor && _items.IndexOf(anchor) is var from and >= 0)
            {
                int to = _items.IndexOf(clicked);
                if (to >= 0 && list.SelectedItems.Count != Math.Abs(to - from) + 1)
                {
                    if (!ctrl) list.SelectedItems.Clear();
                    list.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(Math.Min(from, to), (uint)(Math.Abs(to - from) + 1)));
                }
                return;
            }
            if (ctrl && !shift && clicked is not null && e.RemovedItems.Count > 0)
            {
                foreach (object removed in e.RemovedItems)
                {
                    if (!list.SelectedItems.Contains(removed)) list.SelectedItems.Add(removed);
                }
            }
            if (!shift && clicked is not null) _selectionAnchor = clicked;
        }
        finally
        {
            _adjustingSelection = false;
        }
    }

    // ---------------------------------------------------------------- selection box

    private Windows.Foundation.Point? _marqueeStart;
    private bool _marqueeShown;
    private MarqueeSelection<FileItem>? _marqueeSelection;

    internal bool IsSelectingWithMouse => _marqueeStart is not null;

    /// <summary>A left press on the empty part of the list starts a selection box, as in Explorer.</summary>
    private void Marquee_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase list || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        PointerPoint point = e.GetCurrentPoint(ContentArea);
        if (!point.Properties.IsLeftButtonPressed || OnRowOrScrollBar(e.OriginalSource as DependencyObject, list)) return;
        _marqueeStart = point.Position;
        _marqueeShown = false;
        bool keep = Native.IsKeyDown(0x11) || Native.IsKeyDown(0x10);
        _marqueeSelection = new(keep ? list.SelectedItems.OfType<FileItem>() : []);
        _owner.OnPaneMouseSelectionStarted(this);
        if (!keep) list.SelectedItems.Clear();
        // No pointer capture: the list takes the capture for itself, and losing it would end the box at once.
        list.Focus(FocusState.Pointer);
    }

    private void Marquee_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeStart is not { } start || sender is not ListViewBase list) return;
        PointerPoint current = e.GetCurrentPoint(ContentArea);
        if (!current.Properties.IsLeftButtonPressed)
        {
            // The button was released outside the list, where the release was not seen.
            EndMarquee();
            return;
        }
        Windows.Foundation.Point now = current.Position;
        double left = Math.Min(start.X, now.X), top = Math.Min(start.Y, now.Y);
        double width = Math.Abs(now.X - start.X), height = Math.Abs(now.Y - start.Y);
        if (!_marqueeShown && width < 4 && height < 4) return;
        _marqueeShown = true;
        Canvas.SetLeft(MarqueeRect, left);
        Canvas.SetTop(MarqueeRect, top);
        MarqueeRect.Width = width;
        MarqueeRect.Height = height;
        MarqueeRect.Visibility = Visibility.Visible;
        var box = new Windows.Foundation.Rect(left, top, width, height);
        var intersecting = new List<FileItem>();
        // Virtualized folders can contain hundreds of thousands of items. Hit-test
        // only their realized containers instead of querying every index per move.
        foreach (SelectorItem row in _realizedRows.GetValueOrDefault(list) ?? [])
        {
            if (row.Content is not FileItem item || row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
            Windows.Foundation.Rect bounds = row.TransformToVisual(ContentArea).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
            bounds.Intersect(box);
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) intersecting.Add(item);
        }
        var delta = _marqueeSelection!.Update(intersecting);
        _adjustingSelection = true;
        try
        {
            foreach (FileItem item in delta.Removed) list.SelectedItems.Remove(item);
            foreach (FileItem item in delta.Added) list.SelectedItems.Add(item);
        }
        finally
        {
            _adjustingSelection = false;
        }
        if (delta.Added.Count > 0 || delta.Removed.Count > 0) NotifySelectionChanged();
        e.Handled = true;
    }

    private void Marquee_PointerReleased(object sender, PointerRoutedEventArgs e) => EndMarquee();

    private void EndMarquee()
    {
        if (_marqueeStart is null) return;
        _marqueeStart = null;
        _marqueeShown = false;
        _marqueeSelection = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (!_closed) _owner.OnPaneSelectionChanged(this);
    }

    /// <summary>True when a press landed on a row or on a scroll bar, where it keeps its usual meaning.</summary>
    private static bool OnRowOrScrollBar(DependencyObject? source, ListViewBase list)
    {
        for (DependencyObject? node = source; node is not null && node != list; node = VisualTreeHelper.GetParent(node))
        {
            if (node is SelectorItem or ScrollBar) return true;
        }
        return false;
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
        if (Settings.OpenImagesInViewer && !PathTools.IsShellPlace(path) && FileTypes.IsViewerImage(item.Entry.Extension))
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

    /// <summary>
    /// Selects an item another program asked for through "Show in folder". While the folder is still loading, the
    /// request is kept and applied when the list is shown.
    /// </summary>
    internal void SelectFromShell(string path, bool only)
    {
        FileItem? item = _items.FirstOrDefault(i => PathTools.SameLocation(i.Entry.FullPath, path) || PathTools.SameLocation(i.Entry.FullPath, PathTools.ShellPlace(path)));
        if (item is null)
        {
            if (only) _tab.Selection = [path];
            else _tab.Selection.Add(path);
            return;
        }
        if (only) CurrentList.SelectedItems.Clear();
        if (!CurrentList.SelectedItems.Contains(item)) CurrentList.SelectedItems.Add(item);
        CurrentList.ScrollIntoView(item);
        if (_owner.ActivePane == this && CurrentList.ContainerFromItem(item) is Control row) row.Focus(FocusState.Programmatic);
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
        if (paths.Count == 0 || RefuseShellItems(paths)) return;
        if (!FileClipboard.Put(paths, cut, _owner.Hwnd))
        {
            _owner.ShowMessage(Loc.T("ClipboardBusy"), InfoBarSeverity.Warning);
            return;
        }
        _owner.SetCutPaths(cut ? paths : []);
        _owner.ShowMessage(Loc.F(cut ? "CutDone" : "CopyDone", paths.Count), InfoBarSeverity.Informational);
    }

    /// <summary>
    /// True, with a note to the user, when some items have no path of their own, such as deleted files in the Recycle
    /// Bin. Copying such an item by path would copy Windows' internal file under a wrong name, so it is refused.
    /// </summary>
    private bool RefuseShellItems(IReadOnlyList<string> paths)
    {
        if (!paths.Any(PathTools.IsShellPlace)) return false;
        _owner.ShowMessage(Loc.T("ShellItemsNoCopy"), InfoBarSeverity.Warning);
        return true;
    }

    internal async void Paste(string? into = null)
    {
        string? target = into ?? Folder;
        if (target is null) return;
        var content = FileClipboard.Get(_owner.Hwnd);
        if (content is not { } clip || clip.Paths.Count == 0) return;
        bool completed = await CheckedTransfer(clip.Paths, target, clip.Cut);
        if (clip.Cut && await Task.Run(() => clip.Completion.AllMoved(completed)))
        {
            if (FileClipboard.ClearIfUnchanged(_owner.Hwnd, clip.Completion)) _owner.SetCutPaths([]);
        }
    }

    internal async void TransferToOtherPane(bool move)
    {
        FilePane? other = _owner.OtherPane(this);
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0) return;
        if (other is null)
        {
            // With one pane there is no other pane to go to, so the user picks the folder in the app's own chooser.
            if (await _owner.ChooseFolderAsync(move, paths, Folder) is { } chosen) await CheckedTransfer(paths, chosen, move);
            return;
        }
        if (other.Folder is not { } target)
        {
            _owner.ShowMessage(Loc.T("OtherPaneNoFolder"), InfoBarSeverity.Warning);
            return;
        }
        await CheckedTransfer(paths, target, move);
    }

    /// <summary>Checks a copy or move, then runs it with the app's own windows. Returns false when it did not start.</summary>
    private async Task<bool> CheckedTransfer(IReadOnlyList<string> paths, string target, bool move)
    {
        if (RefuseShellItems(paths) || PathTools.IsShellPlace(target)) return false;
        switch (TransferCheck.Check(paths, target, move))
        {
            case TransferProblem.IntoItself:
                _owner.ShowMessage(Loc.T("IntoItself"), InfoBarSeverity.Warning);
                return false;
            case TransferProblem.AlreadyThere:
            case TransferProblem.Nothing:
                return false;
        }
        return await _owner.TransferAsync(paths, target, move);
    }

    internal async void Delete(bool permanently)
    {
        IReadOnlyList<string> paths = SelectedPaths;
        if (paths.Count == 0) return;
        int index = CurrentList.SelectedIndex;
        if (!await _owner.DeleteAsync(paths, permanently)) return;
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
        _renameStarted = Environment.TickCount64;
        item.IsRenaming = true;
    }

    /// <summary>
    /// When the rename box last opened. Right after it opens, focus can still move on its own: a menu that closes gives
    /// focus back to its button, and the list moves it to the selected row. Such a move must not end the rename, or a
    /// new folder would keep its default name before the user could type one.
    /// </summary>
    private long _renameStarted;

    private static readonly TimeSpan RenameSettleTime = TimeSpan.FromMilliseconds(1000);

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not FileItem item) return;
        box.Text = item.Entry.Name;
        FocusRenameBox(box, item);
        // Focus is given again once a closing menu has put it back on its button.
        DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => FocusRenameBox(box, item));
    }

    private static void FocusRenameBox(TextBox box, FileItem item)
    {
        if (!item.IsRenaming || box.FocusState != FocusState.Unfocused) return;
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
        if (sender is not TextBox { DataContext: FileItem item } box) return;
        if (item.IsRenaming && Environment.TickCount64 - _renameStarted < RenameSettleTime.TotalMilliseconds && FocusWentAstray(box))
        {
            DispatcherQueue.SafeEnqueue(DispatcherQueuePriority.Low, () => FocusRenameBox(box, item));
            return;
        }
        CommitRename(item, box.Text);
    }

    /// <summary>
    /// True when focus left the rename box on its own rather than through the user: no mouse button and no Tab key is
    /// down, and focus went to nothing, the list or one of its rows, or a button, which is where a closing menu or the
    /// list put it. A click anywhere, also on another row or a toolbar button, or Tab, still ends the rename.
    /// </summary>
    private static bool FocusWentAstray(TextBox box)
    {
        const int LeftButton = 0x01, RightButton = 0x02, MiddleButton = 0x04, Tab = 0x09;
        if (Native.IsKeyDown(LeftButton) || Native.IsKeyDown(RightButton) || Native.IsKeyDown(MiddleButton) || Native.IsKeyDown(Tab)) return false;
        object? focused = box.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        return focused is null or ListViewBase or SelectorItem or ButtonBase;
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
        // A rename never replaces another item: a name that is taken is refused here, so Windows is never asked.
        // Changing only the case of the item's own name is allowed.
        bool caseOnly = string.Equals(name, item.Entry.Name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && await Task.Run(() => Path.Exists(Path.Combine(folder, name))))
        {
            _owner.ShowMessage(Loc.F("NameTaken", name), InfoBarSeverity.Warning);
            return;
        }
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
        // A shell place brings Windows' own commands for it, such as "Empty Recycle Bin".
        string? place = Folder ?? (PathTools.IsShellPlace(_tab.Location) && _tab.Search is null ? _tab.Location : null);
        var loading = place is { } folder
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
        // An item of the Recycle Bin or another shell place cannot be copied, moved or renamed by path; Windows' part
        // of the menu brings what it offers instead, such as "Restore".
        if (PathTools.IsShellPlace(item.Entry.FullPath))
        {
            menu.Items.Add(Item("Delete", "", () => Delete(permanently: false), "Del"));
            menu.Items.Add(Tagged(Item("Properties", "", ShowProperties, "Alt+Enter"), ShellMenuMerge.PropertiesTag));
            return menu;
        }
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
            // With one pane the commands ask for the folder, so they are always there; with two they need a folder in the other.
            bool otherFolder = _owner.OtherPane(this) is not { } otherPane || otherPane.Folder is not null;
            bool onePane = _owner.OtherPane(this) is null;
            menu.Items.Add(Item(onePane ? "CopyToFolder" : "CopyToOther","", () => TransferToOtherPane(move: false), "F7", otherFolder));
            menu.Items.Add(Item(onePane ? "MoveToFolder" : "MoveToOther","", () => TransferToOtherPane(move: true), "F8", otherFolder));
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
        (SortColumn? sortedBy, bool sortedDown) = CurrentSort;
        var sortMenu = new MenuFlyoutSubItem { Text = Loc.T("SortBy"), Icon = new FontIcon { Glyph = "" } };
        if (_tab.Search is { } search)
        {
            // Search results start in the order found, which is the relevance for the smart search, and can return to it.
            var found = new RadioMenuFlyoutItem { Text = Loc.T(search.Smart ? "SortRelevance" : "SortFoundOrder"), GroupName = "sort", IsChecked = sortedBy is null };
            found.Click += (_, _) => SetFoundOrder();
            sortMenu.Items.Add(found);
        }
        foreach (SortColumn column in Enum.GetValues<SortColumn>())
        {
            // Search results have no type column, so sorting by type would give an order nobody can see.
            if (_tab.Search is not null && column == SortColumn.Type) continue;
            var option = new RadioMenuFlyoutItem { Text = Loc.T("Column" + column), GroupName = "sort", IsChecked = sortedBy == column };
            option.Click += (_, _) => SetSort(column, sortedDown);
            sortMenu.Items.Add(option);
        }
        sortMenu.Items.Add(new MenuFlyoutSeparator());
        var down = new ToggleMenuFlyoutItem { Text = Loc.T("Descending"), IsChecked = sortedDown, IsEnabled = sortedBy is not null };
        down.Click += (_, _) => SetSort(sortedBy ?? SortColumn.Name, !sortedDown);
        sortMenu.Items.Add(down);
        menu.Items.Add(sortMenu);
        if (_tab.Search is null)
        {
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
        // Items of the Recycle Bin or a phone have no path another folder could take; they are restored or copied
        // through their context menu instead.
        if (paths.Length == 0 || paths.Any(PathTools.IsShellPlace))
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

    /// <summary>
    /// The folder a drop goes into: the folder or drive row under the pointer, otherwise the folder the pane shows. The
    /// row is found by hit-testing the pointer, because the event's source is not reliably the row it is over, which
    /// made a drop on a folder row land in the pane's own folder instead.
    /// </summary>
    private string? DropTarget(DragEventArgs e)
    {
        if (XamlRoot is not null)
        {
            Windows.Foundation.Point point = e.GetPosition(null);
            foreach (UIElement element in VisualTreeHelper.FindElementsInHostCoordinates(point, CurrentList))
            {
                if (element is SelectorItem { Content: FileItem item }) return item.Entry.IsContainer ? item.Entry.FullPath : Folder;
            }
        }
        return Folder;
    }

    /// <summary>True when every dragged item already lies in <paramref name="target"/>, so a move there would do nothing.</summary>
    private static bool AllAlreadyIn(string[]? sources, string target) =>
        sources is { Length: > 0 } && sources.All(s => Path.GetDirectoryName(s.TrimEnd('\\')) is { } parent && PathTools.SameLocation(parent, target));

    private void Content_DragOver(object sender, DragEventArgs e)
    {
        string? target = DropTarget(e);
        bool files = e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Properties.ContainsKey(InternalDragFormat);
        if (target is null || PathTools.IsShellPlace(target) || !files)
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
        // Moving items into the folder they are already in does nothing, so the pointer says so instead of promising it.
        if (move && AllAlreadyIn(internalPaths, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
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
        // Places only Windows lists, such as "shell:RecycleBinFolder", "\\SERVER" or a folder inside a zip file.
        else if (await ShellFolderReader.ResolveAsync(text) is { } place) await NavigateAsync(place);
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

    private void StopSearch_Click(object sender, RoutedEventArgs e)
    {
        // A stopped exact search ends its walk at once; then every hit it found is shown and it ends with "stopped".
        // The smart search just ends.
        if (_tab.Search is { Smart: false } && _searchCts is { } running) running.Cancel();
        else CancelSearch();
    }

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
        _items = new ObservableCollection<FileItem>(SearchOrder(_all));
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
        // The walk is over, because it was done, stopped or reached the hit limit. Whether it was stopped is read now,
        // so a search that completed is never reported as stopped.
        ShowRestAndFinish(cts, stopped: cts.IsCancellationRequested);
    }

    /// <summary>How many hits go into the list per tick while a search runs: fewer when each one is inserted at its sorted place.</summary>
    private int FlushLimit => _tab.Search is { Sort: not null } ? 250 : 3000;

    /// <summary>
    /// Moves found items from the search threads into the list while the search runs, a batch at a time, keeping the
    /// same list so the scroll position, selection and keyboard focus stay. In the order found, hits are added at the
    /// end; with a column chosen, each is inserted at its place, which moves the rows after it, so fewer go in a tick.
    /// </summary>
    private void FlushHits()
    {
        ItemStyle style = RowStyle;
        var batch = new List<FileItem>();
        while (batch.Count < FlushLimit && _pendingHits.TryDequeue(out SearchHit? hit)) batch.Add(HitItem(hit, style));
        AddHits(batch);
        if (_counters is { } counters && _searchCts is not null)
        {
            SearchStatus.Text = Loc.F("SearchProgress", counters.Files.ToString("N0"), counters.Hits.ToString("N0"));
        }
        if (_items.Count > 0) EmptyPanel.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    /// <summary>
    /// Ends a live search whose walk is over: every hit still waiting goes into the list at once, so nothing trickles in
    /// after the search. A few go to their places; many are shown with one rebuild of the list that keeps its scroll
    /// position, selection and keyboard focus.
    /// </summary>
    private void ShowRestAndFinish(CancellationTokenSource cts, bool stopped)
    {
        ItemStyle style = RowStyle;
        var rest = new List<FileItem>();
        while (_pendingHits.TryDequeue(out SearchHit? hit)) rest.Add(HitItem(hit, style));
        if (rest.Count <= FlushLimit)
        {
            AddHits(rest);
        }
        else
        {
            _all.AddRange(rest);
            ShowSearchOrder(keepPlace: true);
        }
        if (_items.Count > 0) EmptyPanel.Visibility = Visibility.Collapsed;
        FinishSearch(cts, null, stopped);
    }

    private FileItem HitItem(SearchHit hit, ItemStyle style) => new(hit.Entry, style)
    {
        Snippet = hit.Snippet,
        Match = (hit.NameMatched ? MatchKind.Name : MatchKind.None) | (hit.ContentMatched ? MatchKind.Words : MatchKind.None),
        FolderText = ShownFolder(hit.Entry.FullPath, ask: false),
    };

    /// <summary>Adds hits to the shown list: at the end in the order found, or each at its place in the chosen column's order.</summary>
    private void AddHits(List<FileItem> hits)
    {
        _all.AddRange(hits);
        Comparison<FileEntry>? order = _tab.Search is { Sort: { } column } search ? EntrySorter.Comparison(column, search.Descending) : null;
        foreach (FileItem item in hits)
        {
            if (order is null) _items.Add(item);
            else _items.Insert(SortedIndex(_items, item.Entry, order), item);
        }
    }

    /// <summary>Ends a search in the status line. <paramref name="stopped"/> overrides reading it from the token.</summary>
    private void FinishSearch(CancellationTokenSource cts, string? note, bool? stopped = null)
    {
        bool wasStopped = stopped ?? cts.IsCancellationRequested;
        if (_searchCts == cts) _searchCts = null;
        _flushTimer.Stop();
        SearchRing.IsActive = false;
        StopSearchButton.Visibility = Visibility.Collapsed;
        string time = (_searchWatch.Elapsed.TotalSeconds < 10 ? _searchWatch.Elapsed.TotalSeconds.ToString("0.00") : _searchWatch.Elapsed.TotalSeconds.ToString("0")) + " s";
        string text = Loc.F(wasStopped ? "SearchStopped" : "SearchDone", _items.Count.ToString("N0"), time);
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
