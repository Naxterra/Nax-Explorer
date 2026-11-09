using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaxExplorer.Core;
using NaxExplorer.Models;
using NaxExplorer.Services;
using NaxExplorer.Shell;

namespace NaxExplorer.Controls;

/// <summary>
/// Shows the selected item: pictures decoded at the panel's size, thumbnails of videos, PDFs and Office files from
/// Windows, the start of text files and the text of documents, and the item's details.
/// </summary>
public sealed partial class PreviewPanel : UserControl
{
    private const int TextChars = 48_000;

    private MainWindow _owner = null!;
    private FileItem? _item;
    private int _version;
    private CancellationTokenSource? _cts;
    private readonly PreviewRefreshState _refresh = new();
    private readonly Dictionary<string, TextBlock> _detailValues = [];
    private int _pictureVersion;
    private bool _thumbnailPending, _forceFullRender;

    public PreviewPanel()
    {
        InitializeComponent();
        OpenButton.Content = Loc.T("Open");
        ViewButton.Content = Loc.T("ViewPicture");
    }

    internal void Attach(MainWindow owner) => _owner = owner;

    internal void PrepareSelection(IReadOnlyList<FileItem> selected, string? folder)
    {
        if (_refresh.Current is { } current && !current.SameSelection(selected.Select(i => i.Entry.FullPath), selected.Count, folder))
            StopBackgroundWork();
    }

    internal void StopBackgroundWork()
    {
        _version++;
        _pictureVersion++;
        _forceFullRender |= _thumbnailPending;
        _refresh.Cancel(_thumbnailPending);
        _thumbnailPending = false;
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>
    /// Shows the selection: one item with its picture, text and details; several items with their counts and total
    /// size; nothing selected with the counts and size of the folder shown, or the space of a drive.
    /// </summary>
    internal async void Show(IReadOnlyList<FileItem> selected, string? folder)
    {
        int side = (int)Math.Min(1200, Math.Max(200, ActualWidth - 24) * (XamlRoot?.RasterizationScale ?? 1));
        var request = new PreviewRequest(selected.Select(i => new PreviewEntry(i.Entry, i.DisplayName, i.FolderText)), folder, side, XamlRoot?.RasterizationScale ?? 1);
        bool reuseTotals = !_forceFullRender && _refresh.Current?.CanReuseTotals(request) == true && _detailValues.ContainsKey("DetailTotal");
        _item = selected.Count == 1 ? selected[0] : null;
        if (_refresh.Begin(request) is not { } generation) return;
        _forceFullRender = false;
        int version = ++_version;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            if (reuseTotals)
            {
                if (_item is { } sameFolder)
                {
                    if (sameFolder.Entry.ModifiedUtc != default && _detailValues.TryGetValue("DetailModified", out var modified))
                        modified.Text = sameFolder.Entry.ModifiedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
                    if (!await ShowFolderContentsAsync(sameFolder.Entry.FullPath, version, cts.Token)) return;
                }
                IReadOnlyList<string> paths = selected.Count > 0 ? selected.Select(i => i.Entry.FullPath).ToList() : [folder!];
                await ShowTotalsAsync(paths, version, cts.Token, selection: selected.Count > 0);
            }
            else
            {
                _pictureVersion++;
                _thumbnailPending = false;
                await RenderAsync(selected, folder, _item, version, cts);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (version == _version)
            {
                _cts = null;
                if (_refresh.Complete(generation)) _owner.RefreshPreviewSoon();
            }
            cts.Dispose();
        }
    }

    private async Task RenderAsync(IReadOnlyList<FileItem> selected, string? folder, FileItem? item, int version, CancellationTokenSource cts)
    {
        PreviewImage.Source = null;
        TextScroller.Visibility = Visibility.Collapsed;
        TextContent.Text = "";
        Details.Children.Clear();
        Details.RowDefinitions.Clear();
        _detailValues.Clear();
        Busy.IsActive = false;
        if (item is null)
        {
            Actions.Visibility = Visibility.Collapsed;
            PictureArea.Visibility = Visibility.Collapsed;
            if (selected.Count > 1)
            {
                NameText.Text = Loc.F("SelectedCount", selected.Count);
                int folders = selected.Count(i => i.Entry.IsContainer);
                AddDetail("DetailSelection", Loc.F("Contains", folders, selected.Count - folders));
                if (selected.All(i => i.Entry.Kind != EntryKind.Drive && !PathTools.IsShellPlace(i.Entry.FullPath)))
                {
                    // Selected cloud folders whose list is not on this device are not looked into, so selecting them
                    // never makes the sync app fetch anything; the totals say how many were left out.
                    await ShowTotalsAsync(selected.Select(i => i.Entry.FullPath).ToList(), version, cts.Token, selection: true);
                }
            }
            else if (folder is null || PathTools.IsThisPc(folder))
            {
                NameText.Text = Loc.T("ThisPC");
            }
            else
            {
                NameText.Text = FilePane.PlaceName(folder);
                if (PathTools.IsShellPlace(folder))
                {
                    // Virtual folders have a shell picture and name, but no filesystem tree to measure.
                    PictureArea.Visibility = Visibility.Visible;
                    ShowShellThumbnail(folder, 256);
                    return;
                }
                // A whole drive would take minutes to count and keep the disk busy; its used and free space say the same at once.
                if (PathTools.Parent(folder) is { } parent && PathTools.IsThisPc(parent)) await ShowDriveSpaceAsync(folder, version);
                else await ShowTotalsAsync(new[] { folder }, version, cts.Token);
            }
            return;
        }
        FileEntry entry = item.Entry;
        NameText.Text = item.DisplayName;
        PictureArea.Visibility = Visibility.Visible;
        SetPicture(item.Icon);
        Actions.Visibility = Visibility.Visible;
        bool picture = entry.Kind == EntryKind.File && FileTypes.IsViewerImage(entry.Extension);
        ViewButton.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Content = entry.IsContainer ? Loc.T("OpenFolder") : Loc.T("Open");

        AddDetail("DetailType", item.TypeText);
        if (entry.Kind == EntryKind.File) AddDetail("DetailSize", $"{SizeFormat.Format(entry.Size)}  ({entry.Size.ToString("N0", CultureInfo.CurrentCulture)} B)");
        if (entry.Kind == EntryKind.Drive && entry.Size > 0) AddDetail("DetailSpace", item.SizeText);
        if (entry.ModifiedUtc != default) AddDetail("DetailModified", entry.ModifiedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));
        if (entry.CreatedUtc != default) AddDetail("DetailCreated", entry.CreatedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));
        if (item.FolderText.Length > 0) AddDetail("DetailFolder", item.FolderText);
        if (item.Cloud != CloudState.None) AddDetail("DetailCloud", item.CloudText);

        int side = (int)Math.Min(1200, Math.Max(200, ActualWidth - 24) * (XamlRoot?.RasterizationScale ?? 1));
        if (PathTools.IsShellPlace(entry.FullPath))
        {
            // An item of the Recycle Bin, a phone or another shell place has no path to read; Windows draws its picture.
            ShowShellThumbnail(entry.FullPath, entry.IsContainer ? 256 : side);
            return;
        }
        if (entry.Kind == EntryKind.File && entry.IsOnlineOnly)
        {
            // Reading the picture or text would download the file; it is downloaded only when the user opens it.
            AddDetail("DetailNote", Loc.T("OnlineOnlyNote"));
            ShowShellThumbnail(entry.FullPath, side);
            return;
        }
        if (picture)
        {
            Busy.IsActive = true;
            try
            {
                LoadedImage image = await ImageLoader.LoadAsync(entry.FullPath, side, cts.Token);
                if (version != _version) return;
                SetPicture(image.Source, image.Width, image.Height);
                AddDetail("DetailDimensions", $"{image.OriginalWidth} × {image.OriginalHeight}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (version != _version) return;
                AddDetail("DetailNote", ImageLoader.ErrorText(entry.FullPath, ex));
                ShowShellThumbnail(entry.FullPath, side);
            }
            finally
            {
                if (version == _version) Busy.IsActive = false;
            }
            return;
        }
        if (entry.Kind == EntryKind.File)
        {
            ShowShellThumbnail(entry.FullPath, side);
            TextFormat format = TextExtractor.FormatOf(entry.Extension);
            if (format != TextFormat.None || entry.Size < 4 * 1024 * 1024)
            {
                string? text = await Task.Run(() => ReadText(entry.FullPath, format), cts.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
                if (version != _version || string.IsNullOrWhiteSpace(text)) return;
                TextContent.Text = text;
                TextScroller.Visibility = Visibility.Visible;
            }
            return;
        }
        ShowShellThumbnail(entry.FullPath, 256);
        if (entry.Kind == EntryKind.Folder)
        {
            if (await ShowFolderContentsAsync(entry.FullPath, version, cts.Token))
                await ShowTotalsAsync(new[] { entry.FullPath }, version, cts.Token, selection: true);
        }
    }

    private async Task<bool> ShowFolderContentsAsync(string path, int version, CancellationToken token)
    {
        // Recheck the live attributes before enumerating, including on a totals-only refresh.
        bool unfetched = await Task.Run(() => CloudFiles.IsUnfetchedFolder(path), token);
        if (version != _version) return false;
        if (unfetched)
        {
            ExistingOrAddDetail("DetailNote", Loc.T("CloudFolderNotFetched")).Text = Loc.T("CloudFolderNotFetched");
            SetDetailVisible("DetailNote", true);
            foreach (string key in new[] { "DetailContains", "DetailTotal", "DetailSize", "DetailOnlineOnly" }) SetDetailVisible(key, false);
            return false;
        }
        (int folders, int files, bool more) = await Task.Run(() => Count(path), token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (0, 0, false), TaskScheduler.Default);
        if (version != _version) return false;
        ExistingOrAddDetail("DetailContains", "").Text = Loc.F(more ? "ContainsMore" : "Contains", folders, files);
        SetDetailVisible("DetailContains", true);
        SetDetailVisible("DetailNote", false);
        return true;
    }

    /// <summary>Counts already done, kept for two minutes, so going back to a folder does not count it again.</summary>
    private readonly FolderTotalsCache _totals = new();

    internal bool InvalidateTotals(string folder, FolderChanges? changes)
    {
        _totals.Invalidate();
        var action = _refresh.Invalidate(folder, changes);
        if (action == PreviewRefreshAction.Reload && _refresh.Current?.CountsContent != true) StopBackgroundWork();
        return action == PreviewRefreshAction.Reload;
    }

    internal void Reload()
    {
        _forceFullRender = true;
        _totals.Invalidate();
        _refresh.Invalidate();
        StopBackgroundWork();
    }

    /// <summary>
    /// Adds the number of all folders and files below the paths and their total size. Large folders are counted in the
    /// background and the numbers grow while the count runs; selecting something else stops it.
    /// </summary>
    private async Task ShowTotalsAsync(IReadOnlyList<string> paths, int version, CancellationToken token, bool selection = false)
    {
        TextBlock total = ExistingOrAddDetail("DetailTotal", Loc.T("Counting"));
        TextBlock size = ExistingOrAddDetail("DetailSize", "…");
        void Show(FolderTotals totals)
        {
            if (version != _version) return;
            SetDetailVisible("DetailTotal", true);
            SetDetailVisible("DetailSize", true);
            string running = totals.Complete ? "" : " …";
            long unfetched = totals.UnfetchedFolders;
            total.Text = Loc.F("Contains", totals.Folders, totals.Files)
                + (unfetched > 0 ? Loc.F("UnfetchedNote", unfetched) : "")
                + running;
            size.Text = $"{SizeFormat.Format(totals.Bytes)}  ({totals.Bytes.ToString("N0", CultureInfo.CurrentCulture)} B){running}";
            // In a cloud folder: how much of it is only online, which opening would download.
            if (totals.OnlineFiles == 0)
            {
                SetDetailVisible("DetailOnlineOnly", false);
                return;
            }
            TextBlock online = ExistingOrAddDetail("DetailOnlineOnly", "");
            SetDetailVisible("DetailOnlineOnly", true);
            online.Text = Loc.F("OnlineOnlyTotals", totals.OnlineFiles, SizeFormat.Format(totals.OnlineBytes)) + running;
        }
        string key = string.Join("|", paths);
        long generation = _totals.Generation;
        if (_totals.TryGet(key, out var known))
        {
            Show(known!);
            return;
        }
        if (paths.Any(FolderStats.IsNetworkPath))
        {
            // A folder on another computer can keep the network busy for minutes, so it is counted only on request.
            total.Text = "";
            size.Text = "";
            var start = new HyperlinkButton { Content = Loc.T("CountNow"), Padding = new Thickness(0), MinHeight = 0, FontSize = 12 };
            Grid.SetRow(start, Grid.GetRow(total));
            Grid.SetColumn(start, 1);
            Details.Children.Add(start);
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            start.Click += (_, _) => go.TrySetResult();
            using (token.Register(() => go.TrySetCanceled()))
            {
                try
                {
                    await go.Task;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
            Details.Children.Remove(start);
            total.Text = Loc.T("Counting");
            size.Text = "…";
        }
        try
        {
            FolderTotals totals = await Task.Run(() => FolderStats.Measure(paths, token, partial => DispatcherQueue.SafeEnqueue(() => Show(partial)), skipUnfetchedRoots: selection), token);
            _totals.Remember(key, totals, generation);
            Show(totals);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Adds the used and free space of a drive, asked off the UI thread because a network drive can be slow.</summary>
    private async Task ShowDriveSpaceAsync(string root, int version)
    {
        TextBlock used = AddDetail("DetailUsed", "…");
        (long total, long free) = await Task.Run(() =>
        {
            try
            {
                var drive = new DriveInfo(root);
                return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : (0L, 0L);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return (0L, 0L);
            }
        });
        if (version != _version) return;
        if (total <= 0)
        {
            used.Text = "–";
            return;
        }
        used.Text = Loc.F("UsedOf", SizeFormat.Format(total - free), SizeFormat.Format(total));
        AddDetail("DetailFree", SizeFormat.Format(free));
    }

    private void ShowShellThumbnail(string path, int side)
    {
        int pictureVersion = _pictureVersion;
        _thumbnailPending = true;
        ImageCache.RequestLarge(path, Math.Min(side, 768), DispatcherQueue, () => pictureVersion == _pictureVersion, image =>
        {
            if (pictureVersion != _pictureVersion) return;
            _thumbnailPending = false;
            if (image is Microsoft.UI.Xaml.Media.Imaging.BitmapSource bitmap) SetPicture(bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
        });
    }

    /// <summary>
    /// Shows a picture no larger than its own pixels, so a small icon stays sharp instead of being blown up to the
    /// width of the panel. Without a known size it is shown as a small icon.
    /// </summary>
    private void SetPicture(Microsoft.UI.Xaml.Media.ImageSource? source, int pixelWidth = 0, int pixelHeight = 0)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        PreviewImage.MaxWidth = pixelWidth > 0 ? DpiMetrics.LogicalPixels(pixelWidth, scale) : 64;
        PreviewImage.MaxHeight = pixelHeight > 0 ? DpiMetrics.LogicalPixels(pixelHeight, scale) : 64;
        PreviewImage.Source = source;
    }

    private static string? ReadText(string path, TextFormat format)
    {
        if (format is TextFormat.Plain or TextFormat.None)
        {
            string? text = TextSniffer.ReadPreview(path, TextChars, out bool truncated);
            return text is null ? null : truncated ? text + "\n…" : text;
        }
        return TextExtractor.Extract(path, TextChars);
    }

    private static (int Folders, int Files, bool More) Count(string folder)
    {
        int folders = 0, files = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
        {
            if (Directory.Exists(entry)) folders++;
            else files++;
            if (folders + files >= 10_000) return (folders, files, true);
        }
        return (folders, files, false);
    }

    private TextBlock AddDetail(string labelKey, string value)
    {
        int row = Details.RowDefinitions.Count;
        Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = Loc.T(labelKey), Style = (Style)Application.Current.Resources["TertiaryTextStyle"], FontSize = 12 };
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
        Grid.SetRow(label, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        Details.Children.Add(label);
        Details.Children.Add(text);
        _detailValues[labelKey] = text;
        return text;
    }

    private TextBlock ExistingOrAddDetail(string key, string value) => _detailValues.TryGetValue(key, out var text) ? text : AddDetail(key, value);

    private void SetDetailVisible(string key, bool visible)
    {
        if (!_detailValues.TryGetValue(key, out var value)) return;
        int row = Grid.GetRow(value);
        foreach (var child in Details.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == row))
            child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        if (_item.Entry.IsContainer) _owner.ActivePane.OpenItem(_item);
        else ShellActions.Open(_item.Entry.FullPath, _owner.Hwnd);
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not null) _owner.ActivePane.ShowInViewer(_item);
    }

    private void PictureArea_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_item is { Entry.Kind: EntryKind.File } item && FileTypes.IsViewerImage(item.Entry.Extension)) _owner.ActivePane.ShowInViewer(item);
    }
}
