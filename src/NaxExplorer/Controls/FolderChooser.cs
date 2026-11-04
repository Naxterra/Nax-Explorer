using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Core;
using NaxExplorer.Services;
using Windows.System;

namespace NaxExplorer.Controls;

/// <summary>
/// The app's own window for choosing where "Copy to" and "Move to" go when only one pane is open. It shows the
/// sidebar's places on the left and the subfolders of the current folder on the right. A click chooses a folder, a
/// double click opens it, and the address box takes a typed path. It only lists folders and never opens a file, so it
/// never makes a cloud app download anything.
/// </summary>
internal sealed partial class FolderChooser : ContentDialog
{
    /// <summary>The folder chosen last in this session, which the next "Copy to" offers first.</summary>
    private static string? s_last;

    private readonly ListingOptions _listing;
    private readonly TextBox _address = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _up = new() { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 7, 10, 7) };
    private readonly Button _newFolder = new() { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 7, 10, 7) };
    private readonly ListView _folders = new() { SelectionMode = ListViewSelectionMode.Single, IsDoubleTapEnabled = true };
    private readonly TextBlock _target = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly Brush _folderBrush;
    private readonly string _start;
    private string? _current;
    private int _version;

    /// <summary>The chosen folder after the dialog closed with the copy or move button, otherwise null.</summary>
    public string? Chosen { get; private set; }

    public FolderChooser(bool move, IReadOnlyList<string> paths, string? paneFolder, IEnumerable<SidebarEntry> places, ListingOptions listing, bool dark)
    {
        _listing = listing;
        _folderBrush = new SolidColorBrush(Palette.Of(FileCategory.Folder, dark));
        _start = s_last ?? paneFolder ?? PathTools.ThisPc;
        string what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Loc.F("ItemCount", paths.Count);
        Title = Loc.F(move ? "MoveToTitle" : "CopyToTitle", what);
        PrimaryButtonText = Loc.T(move ? "MoveHere" : "CopyHere");
        CloseButtonText = Loc.T("Cancel");
        // Enter belongs to the address box and the list, so it never starts a copy by accident.
        DefaultButton = ContentDialogButton.None;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 960.0;
        IsPrimaryButtonEnabled = false;

        ToolTipService.SetToolTip(_up, Loc.T("UpTip"));
        ToolTipService.SetToolTip(_newFolder, Loc.T("NewFolderName"));
        _up.Click += (_, _) => GoUp();
        _newFolder.Flyout = NewFolderFlyout();
        _address.KeyDown += Address_KeyDown;
        _folders.SelectionChanged += (_, _) => ShowTarget();
        // The first click of a double click already chose the folder, so the chosen one is the one to open.
        _folders.DoubleTapped += (_, _) =>
        {
            if (_folders.SelectedItem is ListViewItem { Tag: string path }) _ = NavigateAsync(path);
        };
        _folders.KeyDown += Folders_KeyDown;
        PrimaryButtonClick += (_, _) =>
        {
            Chosen = Target;
            s_last = Chosen;
        };

        var placeList = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        placeList.Items.Add(Row("", Loc.T("ThisPC"), PathTools.ThisPc, null));
        foreach (SidebarEntry place in places)
        {
            if (!place.OpensInExplorer) placeList.Items.Add(Row(place.Glyph, place.Label, place.Location, place.Brush));
        }
        placeList.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: string path }) _ = NavigateAsync(path);
        };

        var bar = new Grid { ColumnSpacing = 6 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_up);
        Grid.SetColumn(_address, 1);
        bar.Children.Add(_address);
        Grid.SetColumn(_newFolder, 2);
        bar.Children.Add(_newFolder);

        var right = new Grid { RowSpacing = 8 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(bar);
        Grid.SetRow(_folders, 1);
        right.Children.Add(_folders);
        Grid.SetRow(_note, 2);
        right.Children.Add(_note);
        var hint = new TextBlock { Text = Loc.T("ChooseFolderHint"), FontSize = 12, Style = (Style)Application.Current.Resources["TertiaryTextStyle"] };
        Grid.SetRow(hint, 3);
        right.Children.Add(hint);
        Grid.SetRow(_target, 4);
        right.Children.Add(_target);

        var layout = new Grid { ColumnSpacing = 12, Width = 860, Height = 460 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(placeList);
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        Content = layout;

        Opened += async (_, _) =>
        {
            // The last destination may be gone or offline by now; then the pane's own folder, then This PC. The fallback
            // happens only while the user has not gone anywhere else in the meantime (a result of null).
            if (await NavigateAsync(_start) != false) return;
            if (paneFolder is not null && await NavigateAsync(paneFolder) != false) return;
            await NavigateAsync(PathTools.ThisPc);
        };
    }

    /// <summary>The folder the copy or move goes to: the chosen subfolder, or else the open folder itself.</summary>
    private string? Target
    {
        get
        {
            if (_folders.SelectedItem is ListViewItem { Tag: string chosen }) return chosen;
            return _current is null || PathTools.IsThisPc(_current) ? null : _current;
        }
    }

    private void ShowTarget()
    {
        string? target = Target;
        IsPrimaryButtonEnabled = target is not null;
        _target.Text = target is null ? "" : Loc.F("Destination", target);
    }

    /// <summary>
    /// Shows a folder's subfolders, or the drives for This PC. Returns true when it is shown, false when it cannot be
    /// read, and null when another navigation started meanwhile, so this one no longer counts either way.
    /// </summary>
    private async Task<bool?> NavigateAsync(string location)
    {
        int version = ++_version;
        bool thisPc = PathTools.IsThisPc(location);
        List<(string Glyph, string Label, string Path, Brush? Brush)> rows;
        try
        {
            rows = await Task.Run(() => thisPc ? Drives() : Subfolders(location));
        }
        catch (Exception ex)
        {
            // Any failure to read a folder, expected or not, is shown here; this runs from event handlers that must
            // never let an exception end the app.
            if (ex is not (IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)) CrashLog.Write(ex);
            if (version != _version) return null;
            ShowNote(ex.Message);
            return false;
        }
        if (version != _version) return null;
        _current = location;
        _address.Text = thisPc ? Loc.T("ThisPC") : location;
        _up.IsEnabled = !thisPc;
        _newFolder.IsEnabled = !thisPc;
        _folders.Items.Clear();
        foreach (var row in rows) _folders.Items.Add(Row(row.Glyph, row.Label, row.Path, row.Brush));
        if (rows.Count == 0) ShowNote(Loc.T("NoSubfolders"));
        else _note.Visibility = Visibility.Collapsed;
        ShowTarget();
        return true;
    }

    private void ShowNote(string text)
    {
        _note.Text = text;
        _note.Visibility = Visibility.Visible;
    }

    private List<(string, string, string, Brush?)> Subfolders(string folder) =>
        DirectoryReader.Read(folder, _listing)
            .Where(e => e.Kind == EntryKind.Folder)
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => ("", e.DisplayName, e.FullPath, (Brush?)_folderBrush))
            .ToList();

    private static List<(string, string, string, Brush?)> Drives() =>
        DirectoryReader.ReadDrives()
            .Select(d => (d.DriveType switch { DriveType.Network => "", DriveType.Removable => "", DriveType.CDRom => "", _ => "" },
                $"{(d.Label.Length > 0 ? d.Label : Loc.Drive(d.DriveType))} ({d.Name})", d.FullPath, (Brush?)null))
            .ToList();

    private ListViewItem Row(string glyph, string label, string path, Brush? brush)
    {
        // The path sits on the row and on its content, because a click on a list reports either of them.
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Tag = path };
        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        if (brush is not null) icon.Foreground = brush;
        line.Children.Add(icon);
        line.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new ListViewItem { Content = line, Tag = path };
        ToolTipService.SetToolTip(item, PathTools.IsThisPc(path) ? label : path);
        return item;
    }

    private void GoUp()
    {
        if (_current is null || PathTools.IsThisPc(_current)) return;
        string? parent = Path.GetDirectoryName(_current.TrimEnd('\\'));
        _ = NavigateAsync(string.IsNullOrEmpty(parent) ? PathTools.ThisPc : PathTools.Normalize(parent, null) ?? PathTools.ThisPc);
    }

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        string typed = _address.Text.Trim();
        if (string.Equals(typed, Loc.T("ThisPC"), StringComparison.CurrentCultureIgnoreCase))
        {
            _ = NavigateAsync(PathTools.ThisPc);
            return;
        }
        string? full = PathTools.Normalize(typed, _current is null || PathTools.IsThisPc(_current) ? null : _current);
        if (full is null) ShowNote(Loc.F("FolderNotFound", typed));
        else _ = NavigateAsync(full);
    }

    private void Folders_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _folders.SelectedItem is ListViewItem { Tag: string path })
        {
            e.Handled = true;
            _ = NavigateAsync(path);
        }
        else if (e.Key == VirtualKey.Back)
        {
            e.Handled = true;
            GoUp();
        }
    }

    /// <summary>A small box for the name of a new folder inside the open folder; the new folder is chosen at once.</summary>
    private Flyout NewFolderFlyout()
    {
        var name = new TextBox { Width = 260 };
        var create = new Button { Content = Loc.T("Create"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var problem = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 8 };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(name);
        line.Children.Add(create);
        panel.Children.Add(line);
        panel.Children.Add(problem);
        var flyout = new Flyout { Content = panel };
        // Every question to the disk runs off the UI thread, because the open folder may be on a share that is slow to
        // answer, and the window must not freeze meanwhile.
        int opening = 0;
        flyout.Opening += async (_, _) =>
        {
            int thisOpening = ++opening;
            problem.Visibility = Visibility.Collapsed;
            if (_current is not { } folder || PathTools.IsThisPc(folder)) return;
            string wanted = Loc.T("NewFolderName");
            name.Text = wanted;
            name.SelectAll();
            string free = await Task.Run(() =>
            {
                try
                {
                    return NameRules.Unused(wanted, isFolder: true, n => Path.Exists(Path.Combine(folder, n)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return wanted;
                }
            });
            // Only for the latest opening, and only while the user has not started typing a name of their own.
            if (thisOpening == opening && name.Text == wanted && free != wanted)
            {
                name.Text = free;
                name.SelectAll();
            }
        };
        bool creating = false;
        async void Create()
        {
            if (creating || _current is not { } folder || PathTools.IsThisPc(folder)) return;
            int version = _version;
            string wanted = name.Text.Trim();
            NameProblem check = NameRules.Check(wanted);
            string? message = check != NameProblem.None ? Loc.T("Name" + check) : null;
            string created = Path.Combine(folder, wanted);
            if (message is null)
            {
                creating = true;
                create.IsEnabled = false;
                message = await Task.Run(() =>
                {
                    try
                    {
                        if (Path.Exists(created)) return Loc.F("NameTaken", wanted);
                        Directory.CreateDirectory(created);
                        return null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        return ex.Message;
                    }
                });
                creating = false;
                create.IsEnabled = true;
            }
            if (message is not null)
            {
                problem.Text = message;
                problem.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            // Should the user have started going to another folder while it was created, even one that has not loaded
            // yet, the chooser follows the user and does not come back here.
            if (version != _version || await NavigateAsync(folder) != true) return;
            _folders.SelectedItem = _folders.Items.OfType<ListViewItem>().FirstOrDefault(i => string.Equals(i.Tag as string, created, StringComparison.OrdinalIgnoreCase));
            _folders.ScrollIntoView(_folders.SelectedItem);
        }
        create.Click += (_, _) => Create();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            Create();
        };
        return flyout;
    }
}
