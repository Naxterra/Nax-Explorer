using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NaxExplorer.Core;
using NaxExplorer.Search;
using NaxExplorer.Services;

namespace NaxExplorer.Models;

/// <summary>How rows are shown: set once per listing, so every row is built the same way.</summary>
internal sealed record ItemStyle(bool Dark, bool Colorful, bool ShowExtensions);

/// <summary>One row of the file list or of the search results. Public, because compiled XAML bindings need that.</summary>
public sealed partial class FileItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private ImageSource? _thumbnail;
    private bool _isCut;
    private bool _isRenaming;
    private ItemStyle _style;

    internal FileItem(FileEntry entry, ItemStyle style)
    {
        Entry = entry;
        _style = style;
        Category = FileTypes.Categorize(entry);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileEntry Entry { get; private set; }

    public FileCategory Category { get; private set; }

    /// <summary>True while the row is on screen; image requests for rows that left the screen are skipped.</summary>
    public volatile bool IsRealized;

    public string DisplayName
    {
        get
        {
            if (Entry.Kind == EntryKind.Drive)
            {
                // Windows' own name, such as "Lokaler Datenträger (C:)"; built from label and letter only when it gave none.
                if (Entry.DisplayName != Entry.Name) return Entry.DisplayName;
                string label = Entry.Label.Length > 0 ? Entry.Label : Loc.Drive(Entry.DriveType);
                return $"{label} ({Entry.Name})";
            }
            if (!_style.ShowExtensions && Entry.Kind == EntryKind.File && Entry.Extension.Length > 0) return Path.GetFileNameWithoutExtension(Entry.Name);
            return Entry.DisplayName;
        }
    }

    public string DateText => Entry.Kind == EntryKind.Drive || Entry.ModifiedUtc == default
        ? ""
        : Entry.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string TypeText => Entry.TypeName ?? ImageCache.TypeName(Entry);

    public string SizeText => Entry.Kind switch
    {
        EntryKind.File => SizeFormat.Format(Entry.Size),
        EntryKind.Drive when Entry.Size > 0 => Loc.F("DriveFree", SizeFormat.Format(Entry.FreeBytes), SizeFormat.Format(Entry.Size)),
        _ => "",
    };

    public Brush NameBrush => Palette.NameBrush(Category, _style.Dark, _style.Colorful);

    public double Opacity => _isCut ? 0.45 : Entry.IsHidden ? 0.65 : 1;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            Notify();
            Notify(nameof(Picture));
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail == value) return;
            _thumbnail = value;
            Notify();
            Notify(nameof(Picture));
        }
    }

    /// <summary>The thumbnail when there is one, otherwise the icon.</summary>
    public ImageSource? Picture => _thumbnail ?? _icon;

    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (_isCut == value) return;
            _isCut = value;
            Notify(nameof(Opacity));
        }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            Notify();
            Notify(nameof(NameVisibility));
        }
    }

    public Visibility NameVisibility => _isRenaming ? Visibility.Collapsed : Visibility.Visible;

    // ---------------------------------------------------------------- drives

    public double UsedFraction => Entry.Kind == EntryKind.Drive && Entry.Size > 0 ? 1 - (double)Entry.FreeBytes / Entry.Size : 0;

    public Visibility UsageVisibility => Entry.Kind == EntryKind.Drive && Entry.Size > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Brush UsageBrush => new SolidColorBrush(Palette.Usage(UsedFraction));

    // ---------------------------------------------------------------- cloud

    /// <summary>Where a cloud file's content is: only online, on this device, or always kept there.</summary>
    public CloudState Cloud => Entry.Kind == EntryKind.Drive ? CloudState.None : CloudFiles.StateOf(Entry.Attributes, CloudRoots.Contains(Entry.FullPath));

    /// <summary>The status mark on the icon, as in Explorer: a cloud, a check mark, or a filled check mark.</summary>
    public string CloudGlyph => Cloud switch
    {
        CloudState.OnlineOnly => "",
        CloudState.OnDevice => "",
        CloudState.AlwaysOnDevice => "",
        _ => "",
    };

    public Brush? CloudBrush => Cloud switch
    {
        CloudState.OnlineOnly => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3B, 0x82, 0xF6)),
        CloudState.None => null,
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x22, 0xC5, 0x5E)),
    };

    public Visibility CloudVisibility => Cloud == CloudState.None ? Visibility.Collapsed : Visibility.Visible;

    public string CloudText => Cloud switch
    {
        CloudState.OnlineOnly => Loc.T("CloudOnlineOnly"),
        CloudState.OnDevice => Loc.T("CloudOnDevice"),
        CloudState.AlwaysOnDevice => Loc.T("CloudAlwaysOnDevice"),
        _ => "",
    };

    // ---------------------------------------------------------------- search results

    public string FolderText { get; init; } = "";

    /// <summary>The text around the match. Matched words are wrapped in the index's mark characters.</summary>
    public string? Snippet { get; init; }

    public MatchKind Match { get; init; }

    public Visibility SnippetVisibility => string.IsNullOrEmpty(Snippet) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NameBadgeVisibility => Match.HasFlag(MatchKind.Name) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WordsBadgeVisibility => Match.HasFlag(MatchKind.Words) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MeaningBadgeVisibility => Match.HasFlag(MatchKind.Meaning) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Takes a newer state of the same file, for example after it was edited, and refreshes the row.</summary>
    public void Update(FileEntry entry)
    {
        bool modified = entry.ModifiedUtc != Entry.ModifiedUtc;
        Entry = entry;
        Category = FileTypes.Categorize(entry);
        if (modified) _thumbnail = null;
        Notify(string.Empty);
    }

    /// <summary>Applies a new look (theme, colours, extensions) to the row.</summary>
    internal void Restyle(ItemStyle style)
    {
        if (style == _style) return;
        _style = style;
        Notify(nameof(DisplayName));
        Notify(nameof(NameBrush));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>What a search in one tab is set to.</summary>
internal sealed class SearchState
{
    public string Query { get; set; } = "";

    /// <summary>True for the AI search, false for the exact live search.</summary>
    public bool Smart { get; set; }

    public bool InNames { get; set; } = true;

    public bool InContents { get; set; } = true;

    public bool Subfolders { get; set; } = true;

    /// <summary>
    /// The column the results are sorted by, or null for the order the search found them in. A chosen column stays for
    /// further searches in the same tab, as in Explorer, until the search is closed or "Relevance" is chosen again.
    /// </summary>
    public SortColumn? Sort { get; set; }

    public bool Descending { get; set; }
}

/// <summary>One tab of a pane: its place, history, view and search.</summary>
internal sealed class PaneTab
{
    public PaneTab(string location)
    {
        Location = location;
        History.Visit(location);
    }

    public string Location { get; set; }

    /// <summary>For a shell place: the place one level up, as Windows named it when the place was listed.</summary>
    public string? ShellParent { get; set; }

    public NavigationHistory History { get; } = new();

    public string View { get; set; } = "details";

    public SortColumn Sort { get; set; } = SortColumn.Name;

    public bool Descending { get; set; }

    /// <summary>The selection when the tab was left, restored when it is shown again.</summary>
    public List<string> Selection { get; set; } = [];

    /// <summary>The search shown in this tab, or null while it shows a folder.</summary>
    public SearchState? Search { get; set; }

    public Microsoft.UI.Xaml.Controls.TabViewItem? Header { get; set; }
}
