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

    public PreviewPanel()
    {
        InitializeComponent();
        OpenButton.Content = Loc.T("Open");
        ViewButton.Content = Loc.T("ViewPicture");
    }

    internal void Attach(MainWindow owner) => _owner = owner;

    internal async void Show(FileItem? item, int selectedCount, string? folder)
    {
        int version = ++_version;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _item = item;
        PreviewImage.Source = null;
        TextScroller.Visibility = Visibility.Collapsed;
        TextContent.Text = "";
        Details.Children.Clear();
        Details.RowDefinitions.Clear();
        Busy.IsActive = false;
        if (item is null)
        {
            Actions.Visibility = Visibility.Collapsed;
            NameText.Text = selectedCount > 1 ? Loc.F("SelectedCount", selectedCount) : folder is null ? Loc.T("ThisPC") : PathTools.TabTitle(folder, folder);
            PictureArea.Visibility = Visibility.Collapsed;
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

        int side = (int)Math.Min(1200, Math.Max(200, ActualWidth - 24) * (XamlRoot?.RasterizationScale ?? 1));
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
                ShowShellThumbnail(entry.FullPath, side, version);
            }
            finally
            {
                if (version == _version) Busy.IsActive = false;
            }
            return;
        }
        if (entry.Kind == EntryKind.File)
        {
            ShowShellThumbnail(entry.FullPath, side, version);
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
        ShowShellThumbnail(entry.FullPath, 256, version);
        if (entry.Kind == EntryKind.Folder)
        {
            (int folders, int files, bool more) = await Task.Run(() => Count(entry.FullPath), cts.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (0, 0, false), TaskScheduler.Default);
            if (version != _version) return;
            AddDetail("DetailContains", Loc.F(more ? "ContainsMore" : "Contains", folders, files));
        }
    }

    private void ShowShellThumbnail(string path, int side, int version)
    {
        ImageCache.RequestLarge(path, Math.Min(side, 768), DispatcherQueue, () => version == _version, image =>
        {
            if (version == _version && image is Microsoft.UI.Xaml.Media.Imaging.BitmapSource bitmap) SetPicture(bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
        });
    }

    /// <summary>
    /// Shows a picture no larger than its own pixels, so a small icon stays sharp instead of being blown up to the
    /// width of the panel. Without a known size it is shown as a small icon.
    /// </summary>
    private void SetPicture(Microsoft.UI.Xaml.Media.ImageSource? source, int pixelWidth = 0, int pixelHeight = 0)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        PreviewImage.MaxWidth = pixelWidth > 0 ? pixelWidth / scale : 64;
        PreviewImage.MaxHeight = pixelHeight > 0 ? pixelHeight / scale : 64;
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

    private void AddDetail(string labelKey, string value)
    {
        int row = Details.RowDefinitions.Count;
        Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = Loc.T(labelKey), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], FontSize = 12 };
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
        Grid.SetRow(label, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        Details.Children.Add(label);
        Details.Children.Add(text);
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
