using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
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
    private static readonly SemaphoreSlim FolderSizeSlots = new(2);
    private readonly Dictionary<FileItem, CancellationTokenSource> _folderSizeRequests = [];
    private (int First, int Last) _folderSizeRange = (-1, -1);
    private double _headerRowWidth = -1, _headerPaneWidth = -1;

    /// <summary>Closed windows release their watchers and searches while the app continues in the background.</summary>
    internal void StopBackgroundWork()
    {
        _closed = true;
        CompositionTarget.Rendering -= TraceSelectionPaint;
        _selectionPaintStarted = 0;
        CancelFolderSizes();
        _loadCts?.Cancel();
        CancelSearch();
        StopWatcher();
        EndMarquee();
        CancelMouseFileDrag();
    }

    public FilePane()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PrepareMarqueeVisual();
            PrepareMarqueeScrollTimer();
        };
        // Incremental rename/reorder updates must not animate the other rows out.
        List.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        Thumbs.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        ContentArea.LayoutUpdated += (_, _) =>
        {
            if (_closed || _tab is null) return;
            AlignColumnHeader();
            UpdateVisibleFolderSizes();
        };
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(250);
        _refreshTimer.IsRepeating = false;
        _refreshTimer.SafeTick(() => _ = RefreshAsync());
        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
        _flushTimer.SafeTick(FlushHits);
        // The list marks pointer presses as handled, so the pane listens to handled ones too, to know it is active.
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: true);
        foreach (ListViewBase list in new ListViewBase[] { List, Thumbs })
        {
            list.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(List_PreviewSpace), handledEventsToo: true);
        }
        ContentArea.AddHandler(PointerPressedEvent, new PointerEventHandler(Marquee_PointerPressed), handledEventsToo: true);
        ContentArea.AddHandler(PointerMovedEvent, new PointerEventHandler(Marquee_PointerMoved), handledEventsToo: true);
        ContentArea.AddHandler(PointerReleasedEvent, new PointerEventHandler(Marquee_PointerReleased), handledEventsToo: true);
        ContentArea.PointerCanceled += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, ContentArea)) { EndMarquee(); CancelMouseFileDrag(); }
        };
        ContentArea.PointerCaptureLost += (_, e) =>
        {
            // Taking capture from a child also raises its lost event through this parent.
            // Only loss by the gesture's owner ends our selection.
            if (ReferenceEquals(e.OriginalSource, ContentArea))
            {
                _selectionTrace?.Record("capture-lost", Stopwatch.GetTimestamp());
                EndMarquee();
                CancelMouseFileDrag();
            }
        };
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
        if (_owner?.IsViewerOpen == true) return;
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
        // F5 recounts the folder sizes too, for changes deeper down that no watcher saw.
        if (Folder is { } shown) FolderSizeCache.Shared.ForgetWithContents(shown);
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
        _refreshVersion++;
        CancelFolderSizes();
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
        _owner.OnPaneReloaded(this);
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
        EndMarquee();
        CancelFolderSizes();
        _headerRowWidth = -1;
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
        if (IsSelectingWithMouse)
        {
            long count = (_marqueeList ?? CurrentList).SelectedRanges.Sum(range => (long)range.Length);
            if (count > 0) parts.Add(Loc.F("Selected", count));
            if (_freeSpace.Length > 0) parts.Add(_freeSpace);
            StatusText.Text = string.Join("   ·   ", parts);
            return;
        }
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
        if (_renaming) return;
        if (IsSelectingWithMouse) { _refreshAfterSelection = true; return; }
        int refreshVersion = ++_refreshVersion;
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
        if (_closed || refreshVersion != _refreshVersion || tab != _tab || !PathTools.SameLocation(location, tab.Location) || tab.Search is not null) return;
        if (IsSelectingWithMouse) { _refreshAfterSelection = true; return; }
        FolderChanges changes = FolderChanges.Compute(_entries, fresh);
        _entries = fresh;
        if (!changes.IsEmpty)
        {
            _owner.OnPaneContentsChanged(location, changes);
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
        // A watcher update can shift every subsequent item index. Finish the gesture
        // before applying it so queued pointer input cannot select different files.
        EndMarquee();
        CancelFolderSizes();
        var byPath = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (FileItem item in _all) byPath[item.Entry.FullPath] = item;
        foreach (FileEntry removed in changes.Removed)
        {
            if (!byPath.Remove(removed.FullPath, out FileItem? item)) continue;
            _all.Remove(item);
            _items.Remove(item);
        }
        // Any change here makes the kept sizes of this folder and the folders above it wrong, but not those of the
        // folders inside it. A changed, removed or added folder, which may replace one of the same name counted
        // earlier, takes the sizes kept for everything inside it along.
        FolderSizeCache.Shared.Forget(_tab.Location);
        foreach (FileEntry changed in changes.Changed)
        {
            if (changed.Kind == EntryKind.Folder) FolderSizeCache.Shared.ForgetWithContents(changed.FullPath);
            if (byPath.TryGetValue(changed.FullPath, out FileItem? item)) item.Update(changed);
        }
        foreach (FileEntry entry in changes.Removed.Concat(changes.Added))
        {
            if (entry.Kind == EntryKind.Folder) FolderSizeCache.Shared.ForgetWithContents(entry.FullPath);
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

    private void AlignColumnHeader()
    {
        if (ColumnHeader.Visibility != Visibility.Visible || List.ItemsPanelRoot is not ItemsStackPanel panel) return;
        if (List.ContainerFromIndex(Math.Max(0, panel.FirstVisibleIndex)) is not SelectorItem { ContentTemplateRoot: Grid row } || row.ActualWidth <= 0) return;
        if (_headerRowWidth == row.ActualWidth && _headerPaneWidth == ContentArea.ActualWidth) return;
        double left = row.TransformToVisual(ContentArea).TransformPoint(new(0, 0)).X;
        ColumnHeader.Margin = new Thickness(left, 0, Math.Max(0, ContentArea.ActualWidth - left - row.ActualWidth), 0);
        _headerRowWidth = row.ActualWidth;
        _headerPaneWidth = ContentArea.ActualWidth;
    }

    // Only on-screen folders are counted; scrolling/navigation cancels obsolete work.
    // Two workers are shared by all panes so a folder full of folders cannot flood the disk.
    private void UpdateVisibleFolderSizes()
    {
        if (_closed || IsSelectingWithMouse || _tab.Search is not null || Folder is not { } shown || !FolderSizeCache.CountsIn(shown)) return;
        var range = CurrentList.ItemsPanelRoot switch
        {
            ItemsStackPanel panel => (panel.FirstVisibleIndex, panel.LastVisibleIndex),
            ItemsWrapGrid panel => (panel.FirstVisibleIndex, panel.LastVisibleIndex),
            _ => (-1, -1),
        };
        if (range == _folderSizeRange) return;
        _folderSizeRange = range;
        var visible = new HashSet<FileItem>();
        for (int i = Math.Max(0, range.Item1); i <= Math.Min(_items.Count - 1, range.Item2); i++)
            if (_items[i].Entry.Kind == EntryKind.Folder) visible.Add(_items[i]);
        foreach (var item in _folderSizeRequests.Keys.Where(item => !visible.Contains(item)).ToArray())
        {
            _folderSizeRequests[item].Cancel();
            _folderSizeRequests.Remove(item);
            item.SetCountingSize(false);
        }
        foreach (FileItem item in visible)
        {
            if (item.HasFolderSize || _folderSizeRequests.ContainsKey(item)) continue;
            // A folder counted a few minutes ago is not counted again.
            if (FolderSizeCache.Shared.TryGet(item.Entry.FullPath, DateTime.UtcNow, out FolderSizeResult? known))
            {
                item.SetFolderSize(known);
                continue;
            }
            item.SetCountingSize(true);
            var request = new CancellationTokenSource();
            _folderSizeRequests.Add(item, request);
            _ = ReadFolderSizeAsync(item, request, FolderSizeCache.Shared.Stamp);
        }
    }

    private void CancelFolderSizes()
    {
        foreach (var (item, request) in _folderSizeRequests)
        {
            request.Cancel();
            item.SetCountingSize(false);
        }
        _folderSizeRequests.Clear();
        _folderSizeRange = (-1, -1);
    }

    /// <param name="startedAt">The cache's stamp before the count started, so a count overtaken by a change, such as a
    /// copy into the folder that was still running, is shown but not kept.</param>
    private async Task ReadFolderSizeAsync(FileItem item, CancellationTokenSource request, long startedAt)
    {
        FolderSizeResult? size = null;
        bool entered = false;
        try
        {
            await FolderSizeSlots.WaitAsync(request.Token);
            entered = true;
            size = await Task.Run(() => FolderSizeReader.Measure(item.Entry.FullPath, request.Token), request.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
        finally
        {
            if (entered) FolderSizeSlots.Release();
            // This continuation is back on the UI thread. A late count cannot update a
            // recycled row or replace a newer request for the same item.
            if (_folderSizeRequests.TryGetValue(item, out var active) && ReferenceEquals(active, request))
            {
                _folderSizeRequests.Remove(item);
                if (!request.IsCancellationRequested && !_closed)
                {
                    item.SetFolderSize(size);
                    FolderSizeCache.Shared.Remember(item.Entry.FullPath, size, DateTime.UtcNow, startedAt);
                }
                else item.SetCountingSize(false);
            }
            request.Dispose();
        }
    }

    private (double Date, double Type, double Size) _columns = (130, 130, 90);

    /// <summary>
    /// Fits the columns to the pane: in a narrow pane the type column goes first, then the date, then the size, so the
    /// name always keeps room to be read.
    /// </summary>
    private void ContentArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EndMarquee();
        _marqueeBoundsDirty = true;
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
        _marqueeBoundsDirty = true;
        if (args.InRecycleQueue)
        {
            if (args.Item is FileItem recycled) recycled.IsRealized = false;
            return;
        }
        if (args.Item is not FileItem item) return;
        args.ItemContainer.DragStarting -= Item_DragStarting;
        args.ItemContainer.DragStarting += Item_DragStarting;
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
        long started = InteractionTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
        NotifySelectionChanged();
        if (started != 0)
        {
            InteractionTrace.Record("selection-handler", started, new { Added = e.AddedItems.Count, Removed = e.RemovedItems.Count, Selected = CurrentList.SelectedItems.Count });
            if (_selectionPaintStarted == 0) CompositionTarget.Rendering += TraceSelectionPaint;
            _selectionPaintStarted = started;
        }
    }

    private long _selectionPaintStarted;

    private void TraceSelectionPaint(object? sender, object e)
    {
        CompositionTarget.Rendering -= TraceSelectionPaint;
        InteractionTrace.Record("selection-next-render", _selectionPaintStarted);
        _selectionPaintStarted = 0;
    }

    private void NotifySelectionChanged()
    {
        UpdateStatus();
        _owner.OnPaneSelectionChanged(this);
    }

    private bool _adjustingSelection;

    // ---------------------------------------------------------------- selection box

    private Windows.Foundation.Point? _marqueeStart;
    private bool _marqueeShown;
    private bool _marqueeKeep;
    private bool _marqueePressedOnItem;
    private readonly SelectionGesture _marqueeGesture = new();
    private DependencyObject? _marqueePressedSource;
    private bool _marqueeClearPending;
    private readonly SelectionGesture _itemGesture = new();
    private ListViewBase? _itemGestureList;
    private DependencyObject? _itemPressedSource;
    private SelectorItem? _itemPressedRow;
    private FileDragSession? _mouseFileDrag;
    private Windows.Foundation.Point _mouseFileDragPoint;
    private DispatcherTimer? _fileDragTimer;
    private string[]? _nativeDragPaths;
    internal bool IsDraggingItems { get; private set; }
    private bool _refreshAfterSelection;
    private RangeSelection? _marqueeSelection;
    private readonly FrameInput<Windows.Foundation.Point> _marqueeInput = new();
    private ListViewBase? _marqueeList;
    private ShapeVisual? _marqueeVisual;
    private CompositionRoundedRectangleGeometry? _marqueeGeometry;
    private readonly List<(int Index, Windows.Foundation.Rect Bounds)> _marqueeBounds = [];
    private bool _marqueeBoundsDirty = true;
    private (int First, int Last, double X, double Y) _marqueeViewport;
    private ScrollViewer? _marqueeScroller;
    private SelectionTrace? _selectionTrace;
    private Windows.Foundation.Point _marqueePointer;
    private Windows.Foundation.Point _marqueeOrigin;
    private DispatcherTimer? _marqueeScrollTimer;
    private long _marqueeScrollTick;
    private MarqueeGrid? _marqueeGrid;
    private readonly Dictionary<int, Windows.Foundation.Rect> _marqueeVisited = [];

    private void RenderMarquee(object? sender, object e)
    {
        _selectionTrace?.Frame();
        ApplyPendingMarquee();
    }
    private void TraceSelectionLayout(object? sender, object e) => _selectionTrace?.Layout();

    internal bool IsSelectingWithMouse => _marqueeStart is not null || IsDraggingItems;
    /// <summary>A left press on the empty part of the list starts a selection box, as in Explorer.</summary>
    private void Marquee_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        ListViewBase list = CurrentList;
        PointerPoint point = e.GetCurrentPoint(ContentArea);
        if (!point.Properties.IsLeftButtonPressed) return;
        // A previous pending click may have lost its child capture outside the
        // window. Retire it before classifying this press, including a name drag.
        EndMarquee();
        CancelMouseFileDrag();
        bool nativeRow = OnRowOrScrollBar(e.OriginalSource as DependencyObject, list, point.Position);
        if (InteractionTrace.Enabled)
            InteractionTrace.Record("mouse-press", data: new { X = point.Position.X, Y = point.Position.Y, NativeRow = nativeRow, Source = e.OriginalSource?.GetType().Name });
        if (nativeRow)
        {
            if (_owner.Settings.DualPane)
            {
                for (var node = e.OriginalSource as DependencyObject; node is not null && node != list; node = VisualTreeHelper.GetParent(node))
                {
                    if (node is TextBox or ButtonBase) break;
                    if (node is SelectorItem { Content: FileItem item } row)
                    {
                        if (item.Entry.Kind != EntryKind.Drive && !item.IsRenaming && !PathTools.IsShellPlace(item.Entry.FullPath))
                        {
                            _itemGesture.Press(point.Position.X, point.Position.Y);
                            _itemGestureList = list;
                            _itemPressedSource = e.OriginalSource as DependencyObject;
                            _itemPressedRow = row;
                            list.CanDragItems = false;
                        }
                        break;
                    }
                }
            }
            return;
        }
        _marqueeStart = point.Position;
        _marqueeGesture.Press(point.Position.X, point.Position.Y);
        _marqueePressedSource = e.OriginalSource as DependencyObject;
        _marqueePressedOnItem = false;
        for (var node = _marqueePressedSource; node is not null && node != ContentArea; node = VisualTreeHelper.GetParent(node))
            if (node is SelectorItem) { _marqueePressedOnItem = true; break; }
        _marqueeList = list;
        // Whitespace belongs to box selection. Keep the native click path, but
        // prevent the list from launching a competing file drag from this press.
        list.CanDragItems = false;
        _marqueeScroller = list.FindDescendant<ScrollViewer>();
        _marqueeOrigin = new(point.Position.X + (_marqueeScroller?.HorizontalOffset ?? 0), point.Position.Y + (_marqueeScroller?.VerticalOffset ?? 0));
        _marqueePointer = point.Position;
        _marqueeGrid = null;
        _marqueeVisited.Clear();
        _marqueeBoundsDirty = true;
        _marqueeInput.Clear();
        _marqueeShown = false;
        bool keep = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)
            || Native.IsPhysicalKeyDown(0x11) || Native.IsPhysicalKeyDown(0x10);
        _marqueeKeep = keep;
    }

    private bool BeginMarquee(PointerRoutedEventArgs e)
    {
        var initial = _marqueeKeep
            ? _marqueeList!.SelectedRanges.Select(range => (range.FirstIndex, range.Length)).ToArray()
            : Array.Empty<(int FirstIndex, uint Length)>();
        _marqueeSelection = new(_marqueeKeep ? initial : []);
        _marqueeClearPending = !_marqueeKeep;
        if (InteractionTrace.Enabled) InteractionTrace.Record("box-start", data: new { Origin = _marqueeOrigin, Initial = initial.Select(run => new { run.FirstIndex, run.Length }).ToArray() });
        MarqueeLayer.Clip = new RectangleGeometry { Rect = new(0, 0, ContentArea.ActualWidth, ContentArea.ActualHeight) };
        if (_marqueeScroller is not null) _marqueeScroller.ViewChanged += Marquee_ViewChanged;
        _selectionTrace = SelectionTrace.Start();
        CompositionTarget.Rendering += RenderMarquee;
        if (_selectionTrace is not null) ContentArea.LayoutUpdated += TraceSelectionLayout;
        _owner.OnPaneMouseSelectionStarted(this);
        CancelFolderSizes();
        for (DependencyObject? node = _marqueePressedSource; node is not null && node != ContentArea; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element) element.ReleasePointerCaptures();
        bool captured = ContentArea.CapturePointer(e.Pointer);
        _selectionTrace?.Record(captured ? "captured" : "capture-failed", Stopwatch.GetTimestamp());
        if (!captured) { EndMarquee(); return false; }
        return true;
    }

    private void Marquee_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_itemGestureList is not null) { MoveMouseFileDrag(e); return; }
        if (_marqueeStart is null || _marqueeList is null) return;
        _selectionTrace?.Input();
        long inputStart = Stopwatch.GetTimestamp();
        PointerPoint current = e.GetCurrentPoint(ContentArea);
        if (!current.Properties.IsLeftButtonPressed)
        {
            // The button was released outside the list, where the release was not seen.
            EndMarquee();
            return;
        }
        if (_marqueeGesture.Move(current.Position.X, current.Position.Y) && !BeginMarquee(e)) return;
        if (!_marqueeGesture.Dragging) return;
        QueueMarquee(current.Position);
        _selectionTrace?.Record("pointer", inputStart);
        e.Handled = true;
    }

    private void QueueMarquee(Windows.Foundation.Point point)
    {
        _marqueePointer = point;
        bool pending = _marqueeInput.Pending;
        _marqueeInput.Submit(point);
        DrawMarquee(point);
        UpdateMarqueeScroll();
        // Request one XAML rendering opportunity; later mouse messages replace the
        // pending point without scheduling more layout or selection work.
        if (!pending) MarqueeLayer.InvalidateArrange();
    }

    private void DrawMarquee(Windows.Foundation.Point now)
    {
        if (_marqueeStart is null) return;
        var start = MarqueeStartInViewport();
        double left = Math.Min(start.X, now.X), top = Math.Min(start.Y, now.Y);
        double width = Math.Abs(now.X - start.X), height = Math.Abs(now.Y - start.Y);
        if (!_marqueeShown && width < 4 && height < 4) return;
        _marqueeShown = true;
        PrepareMarqueeVisual();
        _marqueeVisual!.Offset = new Vector3((float)left, (float)top, 0);
        _marqueeVisual.Size = new Vector2((float)width, (float)height);
        _marqueeGeometry!.Size = new Vector2((float)Math.Max(0, width - 1), (float)Math.Max(0, height - 1));
        _marqueeVisual.Opacity = 1;
    }

    private void PrepareMarqueeVisual()
    {
        if (_marqueeVisual is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(MarqueeLayer).Compositor;
            _marqueeGeometry = compositor.CreateRoundedRectangleGeometry();
            _marqueeGeometry.CornerRadius = new Vector2(2);
            _marqueeGeometry.Offset = new Vector2(0.5f);
            var shape = compositor.CreateSpriteShape(_marqueeGeometry);
            shape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0x33, 0x3B, 0x82, 0xF6));
            shape.StrokeBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
            shape.StrokeThickness = 1;
            _marqueeVisual = compositor.CreateShapeVisual();
            _marqueeVisual.Shapes.Add(shape);
            _marqueeVisual.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(MarqueeLayer, _marqueeVisual);
        }
    }

    private void ApplyPendingMarquee()
    {
        if (_marqueeStart is null || _marqueeList is not { } list || !_marqueeInput.TryTake(out var now) || !_marqueeShown) return;
        var start = MarqueeStartInViewport();
        long traceStart = Stopwatch.GetTimestamp();
        double left = Math.Min(start.X, now.X), top = Math.Min(start.Y, now.Y);
        double width = Math.Abs(now.X - start.X), height = Math.Abs(now.Y - start.Y);
        var box = new Windows.Foundation.Rect(left, top, width, height);
        var visible = list.ItemsPanelRoot switch
        {
            ItemsStackPanel panel => (panel.FirstVisibleIndex, panel.LastVisibleIndex),
            ItemsWrapGrid panel => (panel.FirstVisibleIndex, panel.LastVisibleIndex),
            _ => (-1, -1),
        };
        var viewport = (visible.Item1, visible.Item2, _marqueeScroller?.HorizontalOffset ?? 0, _marqueeScroller?.VerticalOffset ?? 0);
        if (_marqueeBoundsDirty || _marqueeViewport != viewport)
        {
            _marqueeBounds.Clear();
            int first = Math.Max(0, visible.Item1), last = Math.Min(_items.Count - 1, visible.Item2);
            for (int i = first; i <= last; i++)
            {
                if (list.ContainerFromIndex(i) is not FrameworkElement row || row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;
                var bounds = row.TransformToVisual(ContentArea).TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
                _marqueeBounds.Add((i, bounds));
            }
            _marqueeViewport = viewport;
            _marqueeBoundsDirty = false;
        }
        var intersecting = new List<int>();
        foreach (var (index, rowBounds) in _marqueeBounds)
        {
            var bounds = rowBounds;
            bounds.Intersect(box);
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0) intersecting.Add(index);
        }
        double offsetX = _marqueeScroller?.HorizontalOffset ?? 0, offsetY = _marqueeScroller?.VerticalOffset ?? 0;
        foreach (var (index, bounds) in _marqueeBounds)
            _marqueeVisited[index] = new(bounds.X + offsetX, bounds.Y + offsetY, bounds.Width, bounds.Height);
        if (_marqueeGrid is null && _marqueeBounds.Count > 0)
        {
            var first = _marqueeBounds[0];
            if (list == List && _tab.Search is null)
                _marqueeGrid = new(first.Bounds.X + offsetX, first.Bounds.Y + offsetY - first.Index * first.Bounds.Height,
                    first.Bounds.Width, first.Bounds.Height, first.Bounds.Width, first.Bounds.Height, 1);
            else if (list == Thumbs && list.ItemsPanelRoot is ItemsWrapGrid wrap && wrap.Orientation == Orientation.Horizontal)
            {
                // GridView lays out equally sized cells, with any gaps included in the pitch.
                int columns = Math.Max(1, (int)Math.Floor((wrap.ActualWidth) / first.Bounds.Width));
                double pitchX = first.Bounds.Width, pitchY = first.Bounds.Height;
                var adjacent = _marqueeBounds.FirstOrDefault(b => b.Index == first.Index + 1);
                if (adjacent.Bounds.X > first.Bounds.X) pitchX = adjacent.Bounds.X - first.Bounds.X;
                var below = _marqueeBounds.FirstOrDefault(b => b.Bounds.Y > first.Bounds.Y + 1 && Math.Abs(b.Bounds.X - first.Bounds.X) < 1);
                if (below.Bounds.Height > 0)
                {
                    columns = below.Index - first.Index;
                    pitchY = below.Bounds.Y - first.Bounds.Y;
                }
                _marqueeGrid = new(first.Bounds.X + offsetX - first.Index % columns * pitchX,
                    first.Bounds.Y + offsetY - first.Index / columns * pitchY,
                    first.Bounds.Width, first.Bounds.Height, pitchX, pitchY, columns);
            }
        }
        IEnumerable<(int Start, uint Length)> inside;
        if (_marqueeGrid is { } grid)
            inside = grid.Intersect(left + offsetX, top + offsetY, left + offsetX + width, top + offsetY + height, _items.Count);
        else
        {
            var contentBox = new Windows.Foundation.Rect(left + offsetX, top + offsetY, width, height);
            inside = SelectionRuns.FromIndices(_marqueeVisited.Where(pair =>
            {
                var bounds = pair.Value;
                bounds.Intersect(contentBox);
                return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
            }).Select(pair => pair.Key));
        }
        var delta = _marqueeSelection!.Update(inside);
        _selectionTrace?.Record("hit-test", traceStart, _marqueeBounds.Count);
        long selectionStart = Stopwatch.GetTimestamp();
        _adjustingSelection = true;
        try
        {
            if (_marqueeClearPending)
            {
                // One native reset is much faster than DeselectRange over a large
                // materialized selection. Defer it until the first coalesced frame.
                list.SelectedItems.Clear();
                _marqueeClearPending = false;
            }
            foreach (var run in delta.Removed) list.DeselectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(run.Start, run.Length));
            foreach (var run in delta.Added) list.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(run.Start, run.Length));
        }
        finally
        {
            _adjustingSelection = false;
        }
        if (delta.Added.Length > 0 || delta.Removed.Length > 0) NotifySelectionChanged();
        _selectionTrace?.Record("selection", selectionStart, intersecting.Count, (int)delta.Added.Sum(r => (long)r.Length), (int)delta.Removed.Sum(r => (long)r.Length), (int)list.SelectedRanges.Sum(r => (long)r.Length));
        _selectionTrace?.Record("move-total", traceStart);
    }

    private Windows.Foundation.Point MarqueeStartInViewport() => new(
        _marqueeOrigin.X - (_marqueeScroller?.HorizontalOffset ?? 0),
        _marqueeOrigin.Y - (_marqueeScroller?.VerticalOffset ?? 0));

    private void Marquee_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_marqueeStart is not null) QueueMarquee(_marqueePointer);
    }

    private void UpdateMarqueeScroll()
    {
        if (_marqueeScroller is not { } scroll || !_marqueeShown) return;
        double top = scroll.TransformToVisual(ContentArea).TransformPoint(new(0, 0)).Y;
        double speed = MarqueeScroll.Speed(_marqueePointer.Y - top, scroll.ViewportHeight);
        _selectionTrace?.Scroll(_marqueePointer.Y - top, scroll.ViewportHeight, scroll.ScrollableHeight, scroll.VerticalOffset, speed, scroll.ActualHeight);
        if (speed == 0) { _marqueeScrollTimer?.Stop(); return; }
        PrepareMarqueeScrollTimer();
        if (!_marqueeScrollTimer!.IsEnabled)
        {
            _marqueeScrollTick = Stopwatch.GetTimestamp();
            _marqueeScrollTimer.Start();
        }
    }

    private void PrepareMarqueeScrollTimer()
    {
        if (_marqueeScrollTimer is null)
        {
            _marqueeScrollTimer = new DispatcherTimer();
            _marqueeScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _marqueeScrollTimer.Tick += (_, _) => SafeDispatch.Run(() =>
            {
                if (_marqueeStart is null || _marqueeScroller is not { } viewer) return;
                long now = Stopwatch.GetTimestamp();
                double seconds = Math.Min(0.05, Stopwatch.GetElapsedTime(_marqueeScrollTick, now).TotalSeconds);
                _marqueeScrollTick = now;
                double origin = viewer.TransformToVisual(ContentArea).TransformPoint(new(0, 0)).Y;
                double velocity = MarqueeScroll.Speed(_marqueePointer.Y - origin, viewer.ViewportHeight);
                double target = Math.Clamp(viewer.VerticalOffset + velocity * seconds, 0, viewer.ScrollableHeight);
                if (velocity == 0 || Math.Abs(target - viewer.VerticalOffset) < 0.01) { _marqueeScrollTimer.Stop(); return; }
                bool accepted = viewer.ChangeView(null, target, null, disableAnimation: true);
                _selectionTrace?.Record(accepted ? "scroll-requested" : "scroll-rejected", Stopwatch.GetTimestamp());
                QueueMarquee(_marqueePointer);
            });
        }
    }

    private void Marquee_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_itemGestureList is not null) { ReleaseMouseFileDrag(e); return; }
        if (_marqueeStart is null) return;
        bool dragged = _marqueeGesture.Dragging;
        if (dragged)
        {
            QueueMarquee(e.GetCurrentPoint(ContentArea).Position);
            ApplyPendingMarquee();
        }
        bool preserve = _marqueeKeep || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)
            || Native.IsPhysicalKeyDown(0x11) || Native.IsPhysicalKeyDown(0x10);
        if (_marqueeGesture.ClearsSelectionOnRelease(_marqueePressedOnItem, preserve) && _marqueeList is { } list)
        {
            _adjustingSelection = true;
            try { list.SelectedItems.Clear(); }
            finally { _adjustingSelection = false; }
        }
        if (InteractionTrace.Enabled) InteractionTrace.Record("mouse-release", data: new { Dragged = dragged, Point = e.GetCurrentPoint(ContentArea).Position, Selected = _marqueeList?.SelectedRanges.Select(r => new { r.FirstIndex, r.Length }).ToArray() });
        EndMarquee();
        if (dragged) e.Handled = true;
    }

    private void EndMarquee()
    {
        if (_marqueeStart is null) return;
        CompositionTarget.Rendering -= RenderMarquee;
        if (_selectionTrace is not null)
        {
            ContentArea.LayoutUpdated -= TraceSelectionLayout;
            _selectionTrace.Finish();
            _selectionTrace = null;
        }
        _marqueeStart = null;
        _marqueeGesture.Reset();
        if (_marqueeList is { } list) list.CanDragItems = true;
        _marqueeList = null;
        if (_marqueeScroller is not null) _marqueeScroller.ViewChanged -= Marquee_ViewChanged;
        _marqueeScrollTimer?.Stop();
        _marqueeScroller = null;
        _marqueeVisited.Clear();
        _marqueeGrid = null;
        _marqueeInput.Clear();
        _marqueeBounds.Clear();
        _marqueeShown = false;
        _marqueeSelection = null;
        _marqueePressedSource = null;
        _marqueePressedOnItem = false;
        _marqueeClearPending = false;
        if (_marqueeVisual is not null) _marqueeVisual.Opacity = 0;
        ContentArea.ReleasePointerCaptures();
        if (!_closed) NotifySelectionChanged();
        if (!_closed) UpdateVisibleFolderSizes();
        if (_refreshAfterSelection)
        {
            _refreshAfterSelection = false;
            if (!_closed) ScheduleRefresh();
        }
    }

    /// <summary>True when a press landed on a row or on a scroll bar, where it keeps its usual meaning.</summary>
    private bool OnRowOrScrollBar(DependencyObject? source, ListViewBase list, Windows.Foundation.Point point)
    {
        for (DependencyObject? node = source; node is not null && node != list; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar or TextBox or ButtonBase) return true;
            if (node is SelectorItem item)
            {
                if (list != List || item.ContentTemplateRoot is not Grid row) return true;
                // As in Explorer, a file is dragged by its icon and its name. The blank space after the name and the
                // other columns start a selection box when dragged, and select the row when clicked.
                var name = row.Children.OfType<TextBlock>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
                if (name is null || name.Visibility != Visibility.Visible) return true;
                double textLeft = name.TransformToVisual(ContentArea).TransformPoint(new(0, 0)).X;
                return point.X <= textLeft + Math.Min(name.ActualWidth, name.DesiredSize.Width) + 8;
            }
        }
        return false;
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) OpenItem(item);
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _owner.IsViewerOpen) return;
        // Preview routing reaches us before ListView consumes Enter as its own
        // activation key. Embedded editors must retain their normal commit keys.
        if (KeyIsInsideEditor(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
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
        if (e.Handled || _owner.IsViewerOpen) return;
        if (KeyIsInsideEditor(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        if (e.Key != VirtualKey.Space || Native.IsKeyDown(0x11) || _typed.Length > 0 && DateTime.UtcNow - _typedAt < TimeSpan.FromSeconds(1)) return;
        if (SelectedItems is [{ Entry.Kind: EntryKind.File } item] && FileTypes.IsViewerImage(item.Entry.Extension))
        {
            ShowInViewer(item);
            e.Handled = true;
        }
    }

    private static bool KeyIsInsideEditor(DependencyObject? source, DependencyObject? list)
    {
        for (var node = source; node is not null && node != list; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or AutoSuggestBox or PasswordBox or RichEditBox or ButtonBase) return true;
        return false;
    }

    /// <summary>Typing letters jumps to the first name that starts with them.</summary>
    private void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_owner.IsViewerOpen) return;
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
        switch (await Task.Run(() => TransferCheck.Check(paths, target, move)))
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
        if (_renaming) return;
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
    private bool _renaming;
    private int _refreshVersion;

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
        // Let x:Load remove the editor after its key/focus event has unwound.
        // Forcing UpdateLayout here can recycle rows during the rename event.
        FocusListSoon();
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
        string originalPath = item.Entry.FullPath;
        PaneTab tab = _tab;
        _renaming = true;
        _refreshVersion++;
        try
        {
            await ShellActions.RenameAsync(originalPath, name, _owner.Hwnd);
            FileEntry? renamed = await Task.Run(() => !caseOnly && Path.Exists(originalPath) ? null : ReadAsOnDisk(folder, name));
            if (renamed is not null && !_closed && tab == _tab && PathTools.SameLocation(folder, _tab.Location))
                ApplyRenamedRow(item, renamed);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _renaming = false;
        }
        await RefreshAsync();
    }

    private void ApplyRenamedRow(FileItem item, FileEntry renamed)
    {
        FileEntry previous = item.Entry;
        int entryIndex = _entries.FindIndex(entry => PathTools.SameLocation(entry.FullPath, previous.FullPath));
        if (entryIndex < 0 || !_all.Contains(item)) return;
        _entries[entryIndex] = renamed;
        if (_folderSizeRequests.Remove(item, out var folderSize)) folderSize.Cancel();
        item.Update(renamed);
        Comparison<FileEntry> order = EntrySorter.Comparison(_tab.Sort, _tab.Descending);
        Comparison<FileItem> rows = (left, right) => order(left.Entry, right.Entry);
        ListingOrder.Reposition(_all, item, rows);
        if (_filter.Length == 0 || Matches(renamed))
        {
            if (_items.Contains(item)) ListingOrder.Reposition(_items, item, rows);
            else _items.Insert(SortedIndex(_items, renamed, order), item);
            CurrentList.SelectedItem = item;
        }
        else _items.Remove(item);
        if (item.IsRealized)
        {
            ImageCache.RequestIcon(item, IconPixels, DispatcherQueue);
            if (CurrentList == Thumbs) ImageCache.RequestThumbnail(item, ThumbnailPixels, DispatcherQueue);
        }
        _owner.OnPaneContentsChanged(_tab.Location, new FolderChanges([previous], [renamed], []));
        NotifySelectionChanged();
        _folderSizeRange = (-1, -1);
        UpdateVisibleFolderSizes();
    }

    /// <summary>
    /// The item named <paramref name="name"/> in <paramref name="folder"/>, with its name spelled as on disk. After a
    /// rename that only changes the case, the path alone cannot tell whether the rename happened, because Windows finds
    /// the item under either spelling; the folder listing gives the spelling it really has.
    /// </summary>
    private static FileEntry? ReadAsOnDisk(string folder, string name)
    {
        try
        {
            // Asked of the one entry by its exact path, so even a folder with very many items is not listed.
            string? actual = Native.NameOnDisk(Path.Combine(folder, name));
            return actual is null ? null : DirectoryReader.ReadOne(Path.Combine(folder, actual));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
        if (!container && !several) StartMenuPinMenu.Add(menu, item.Entry.FullPath, _owner);
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

    /// <summary>
    /// The folder a mouse drag would drop into at <paramref name="point"/>, with the element to frame: the folder row
    /// under the pointer, or otherwise the list of the folder this pane shows. Null outside this pane.
    /// </summary>
    internal (string Target, FrameworkElement Element)? MouseDropTarget(Windows.Foundation.Point point, UIElement relativeTo)
    {
        if (Visibility != Visibility.Visible || CurrentList.Visibility != Visibility.Visible) return null;
        var bounds = CurrentList.TransformToVisual(relativeTo).TransformBounds(new(0, 0, CurrentList.ActualWidth, CurrentList.ActualHeight));
        if (!bounds.Contains(point)) return null;
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(point, CurrentList))
        {
            if (element is SelectorItem { Content: FileItem { Entry.IsContainer: true } item } row) return (item.Entry.FullPath, row);
            if (element is SelectorItem) break;
        }
        return Folder is { } folder ? (folder, CurrentList) : null;
    }

    private async void MoveMouseFileDrag(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ContentArea);
        if (!point.Properties.IsLeftButtonPressed) { CancelMouseFileDrag(); return; }
        if (_itemGesture.Move(point.Position.X, point.Position.Y))
        {
            if (_itemPressedRow?.Content is not FileItem pressed) { CancelMouseFileDrag(); return; }
            if (!_itemGestureList!.SelectedItems.Contains(pressed))
            {
                // As in Explorer, dragging a row that is not selected selects it, alone or with Ctrl added to the
                // others, so what moves is what the list shows as selected.
                _adjustingSelection = true;
                try
                {
                    if (!(e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || Native.IsPhysicalKeyDown(0x11))) _itemGestureList.SelectedItems.Clear();
                    _itemGestureList.SelectedItems.Add(pressed);
                }
                finally { _adjustingSelection = false; }
            }
            var paths = SelectedPaths;
            _mouseFileDrag = new(paths);
            IsDraggingItems = true;
            _owner.OnPaneMouseSelectionStarted(this);
            CancelFolderSizes();
            for (var node = _itemPressedSource; node is not null && node != ContentArea; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element) element.ReleasePointerCaptures();
            if (!ContentArea.CapturePointer(e.Pointer)) { CancelMouseFileDrag(); return; }
            _fileDragTimer ??= CreateFileDragTimer();
            _fileDragTimer.Start();
            InteractionTrace.Record("mouse-file-drag-start", data: new { Count = paths.Count });
        }
        if (_mouseFileDrag is not { } drag) return;
        _mouseFileDragPoint = _owner.MouseDragPoint(e);
        bool outside = _owner.UpdateMouseFileDrag(this, drag, _mouseFileDragPoint);
        e.Handled = true;
        if (outside && _itemPressedRow is { } container)
        {
            // Hand an outgoing drag to Windows; between-pane drags stay in this
            // pointer route and do not depend on the native drop message loop.
            _nativeDragPaths = drag.Sources.ToArray();
            var nativePoint = e.GetCurrentPoint(container);
            // The drag goes on in Windows' hands, so it is not finished here: folder counting and a deferred refresh
            // keep waiting until the native drag ends.
            EndMouseFileGesture(handOff: true);
            try { FinishItemDrag(await container.StartDragAsync(nativePoint) == DataPackageOperation.Move); }
            catch (Exception ex)
            {
                // This handler is async void, so every failure of the native drag ends here instead of ending the app.
                if (ex is not System.Runtime.InteropServices.COMException) CrashLog.Write(ex);
                FinishItemDrag(moved: false);
                if (!_closed) _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
            }
            finally { _nativeDragPaths = null; }
        }
    }

    private DispatcherTimer CreateFileDragTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => SafeDispatch.Run(() =>
        {
            if (_mouseFileDrag is { } drag) _owner.UpdateMouseFileDrag(this, drag, _mouseFileDragPoint, modifiersOnly: true);
        });
        return timer;
    }

    private async void ReleaseMouseFileDrag(PointerRoutedEventArgs e)
    {
        var drag = _mouseFileDrag;
        if (drag is null) { EndMouseFileGesture(); return; }
        _owner.UpdateMouseFileDrag(this, drag, _owner.MouseDragPoint(e));
        // Preserve the modifiers attached to mouse-up even if the key was
        // released before the queued event reaches this UI thread.
        bool control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || Native.IsPhysicalKeyDown(0x11);
        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || Native.IsPhysicalKeyDown(0x10);
        var choice = drag.Release(control, shift);
        EndMouseFileGesture();
        e.Handled = true;
        if (choice is not { } drop) return;
        InteractionTrace.Record("mouse-file-drop", data: new { drop.Target, drop.Move, Count = drag.Sources.Count });
        try
        {
            // The Recycle Bin in the sidebar deletes what is dropped on it, as a drag from another program does there.
            bool completed = drop.Recycle
                ? await _owner.DeleteAsync(drag.Sources, permanently: false)
                : await CheckedTransfer(drag.Sources, drop.Target, drop.Move);
            InteractionTrace.Record("mouse-file-drop-complete", data: new { Completed = completed, drop.Move, drop.Recycle });
        }
        catch (Exception ex)
        {
            // This handler is async void, so every failure of the drop ends here instead of ending the app.
            if (ex is not (IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)) CrashLog.Write(ex);
            if (!_closed) _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    internal void CancelMouseFileDrag()
    {
        _mouseFileDrag?.Cancel();
        EndMouseFileGesture();
    }

    /// <param name="handOff">True when the drag continues as Windows' own drag, which finishes it when it ends.</param>
    private void EndMouseFileGesture(bool handOff = false)
    {
        bool dragging = _mouseFileDrag is not null;
        _mouseFileDrag = null;
        _itemGesture.Reset();
        if (_itemGestureList is { } list) list.CanDragItems = true;
        _itemGestureList = null;
        _itemPressedRow = null;
        _itemPressedSource = null;
        _fileDragTimer?.Stop();
        _owner?.EndMouseFileDrag(this);
        if (!dragging) return;
        ContentArea.ReleasePointerCaptures();
        if (!handOff) FinishItemDrag(moved: false);
    }

    private async void Item_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        // RequestedOperation describes the data package. The native drag has a
        // separate allowed-operation mask, which must permit both choices before
        // the drag starts so Ctrl/Shift can change the action while it is running.
        e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        var deferral = e.GetDeferral();
        long started = InteractionTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            string[] paths = e.Data.Properties.TryGetValue(InternalDragFormat, out object? value) && value is string[] own
                ? own : _nativeDragPaths ?? SelectedPaths.ToArray();
            if (paths.Length == 0)
            {
                e.Cancel = true;
                return;
            }
            // The app's own panes and sidebar only need the paths. Other programs need storage items, which are ready
            // when the drag starts if they resolve within a second; for very many items or a slow share they are
            // supplied when the drop target asks, so the drag never waits for all of them.
            e.Data.Properties[InternalDragFormat] = paths;
            IStorageItem[]? items = await FileDragPayload.ReadWithinAsync(paths, TimeSpan.FromSeconds(1));
            if (_closed) { e.Cancel = true; return; }
            if (items is not null) e.Data.SetStorageItems(items);
            else e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideStorageItems(request, paths));
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
            InteractionTrace.Record("drag-ready", started, new { Count = paths.Length, Ready = items is not null, Allowed = e.AllowedOperations.ToString(), Requested = e.Data.RequestedOperation.ToString() });
        }
        catch (Exception ex)
        {
            // Every failure ends here: this handler is async void, so an escaping exception would end the app.
            if (ex is not (IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)) CrashLog.Write(ex);
            e.Cancel = true;
            if (!_closed) _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (e.Cancel)
            {
                FinishItemDrag(moved: false);
            }
            deferral.Complete();
        }
    }

    /// <summary>Supplies the storage items of a drag whose items took too long to resolve before it started.</summary>
    private static async void ProvideStorageItems(DataProviderRequest request, string[] paths)
    {
        DataProviderDeferral? deferral = null;
        try
        {
            deferral = request.GetDeferral();
            request.SetData(await FileDragPayload.ReadAsync(paths));
        }
        // An item that vanished meanwhile leaves the target without data, so nothing reaches it at all rather than part
        // of the selection. Every failure ends here: this runs outside any caller, often on a worker thread, where an
        // escaping exception would end the app in the middle of a drop.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        catch (Exception ex) { CrashLog.Write(ex); }
        finally
        {
            try { deferral?.Complete(); }
            catch (Exception ex) { CrashLog.Write(ex); }
        }
    }

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_marqueeStart is not null) { e.Cancel = true; return; }
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
        IsDraggingItems = true;
        _owner.OnPaneMouseSelectionStarted(this);
        CancelFolderSizes();
        InteractionTrace.Record("drag-start", data: new { Count = paths.Length, Control = Native.IsPhysicalKeyDown(0x11), Shift = Native.IsPhysicalKeyDown(0x10) });
    }

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        InteractionTrace.Record("drag-completed", data: new { Result = args.DropResult.ToString() });
        FinishItemDrag(args.DropResult == DataPackageOperation.Move);
    }

    private void FinishItemDrag(bool moved)
    {
        if (!IsDraggingItems) return;
        IsDraggingItems = false;
        bool refresh = moved || _refreshAfterSelection;
        _refreshAfterSelection = false;
        if (_closed) return;
        NotifySelectionChanged();
        UpdateVisibleFolderSizes();
        // A cancelled drag or a copy has not changed the source folder. Avoid
        // rereading a large listing unless a watcher deferred a real change.
        if (refresh) ScheduleRefresh();
        InteractionTrace.Record("drag-finish", data: new { Refresh = refresh });
    }

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
        e.Handled = true;
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
        if (InteractionTrace.Enabled)
        {
            string traceChoice = $"{target}|{e.AcceptedOperation}|{e.Modifiers}";
            if (traceChoice != _lastDropTrace)
            {
                _lastDropTrace = traceChoice;
                InteractionTrace.Record("drag-over", data: new { Target = target, Allowed = e.AllowedOperations.ToString(), Operation = e.AcceptedOperation.ToString(), Modifiers = e.Modifiers.ToString(), Control = Native.IsPhysicalKeyDown(0x11), Shift = Native.IsPhysicalKeyDown(0x10), InternalType = value?.GetType().FullName, Sources = internalPaths?.Length });
            }
        }
        e.DragUIOverride.Caption = Loc.F(move ? "DropMove" : "DropCopy", PlaceName(target));
        e.DragUIOverride.IsCaptionVisible = true;
    }

    /// <summary>Explorer's rule: Shift moves, Ctrl copies, otherwise items on the same drive move and others copy.</summary>
    private static bool DropMoves(DragEventArgs e, string[]? sources, string target)
    {
        return DropPolicy.Moves(e.Modifiers.HasFlag(DragDropModifiers.Control) || Native.IsPhysicalKeyDown(0x11),
            e.Modifiers.HasFlag(DragDropModifiers.Shift) || Native.IsPhysicalKeyDown(0x10), sources, target);
    }

    private string? _lastDropTrace;

    private async void Content_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        string? target = DropTarget(e);
        if (target is null) return;
        DragOperationDeferral deferral = e.GetDeferral();
        // Capture the user's choice before awaiting external storage-item resolution.
        bool control = e.Modifiers.HasFlag(DragDropModifiers.Control) || Native.IsPhysicalKeyDown(0x11);
        bool shift = e.Modifiers.HasFlag(DragDropModifiers.Shift) || Native.IsPhysicalKeyDown(0x10);
        long dropStarted = InteractionTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
        InteractionTrace.Record("drop-input", data: new { Target = target, Control = control, Shift = shift, Modifiers = e.Modifiers.ToString(), Accepted = e.AcceptedOperation.ToString() });
        try
        {
            string[]? internalPaths = e.DataView.Properties.TryGetValue(InternalDragFormat, out object? value) ? value as string[] : null;
            List<string> paths = internalPaths?.ToList() ?? [];
            if (paths.Count == 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            }
            // Decided from the same input as the pointer during the drag: the app's own paths. A drag from another
            // program has none there, so it copies unless Shift was held, exactly as the pointer said.
            bool move = DropPolicy.Moves(control, shift, internalPaths, target);
            InteractionTrace.Record("drop-resolved", dropStarted, new { Count = paths.Count, Move = move });
            if (paths.Count > 0)
            {
                bool completed = await CheckedTransfer(paths, target, move);
                e.AcceptedOperation = completed ? (move ? DataPackageOperation.Move : DataPackageOperation.Copy) : DataPackageOperation.None;
                InteractionTrace.Record("drop-complete", dropStarted, new { Completed = completed, Move = move });
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            _owner.ShowMessage(ex.Message, InfoBarSeverity.Error);
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
        if (hits.Count > 0) EndMarquee();
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
