using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NaxExplorer.Core;
using NaxExplorer.Models;
using NaxExplorer.Shell;
using Windows.UI;

namespace NaxExplorer.Services;

/// <summary>The colours of file kinds: bright tones on dark backgrounds and deeper tones of the same hue on light ones.</summary>
internal static class Palette
{
    private static readonly Dictionary<(FileCategory, bool), SolidColorBrush> Brushes = [];

    public static Color Of(FileCategory category, bool dark) => category switch
    {
        FileCategory.Folder => dark ? Rgb(0xFF, 0xCA, 0x62) : Rgb(0xA8, 0x6B, 0x00),
        FileCategory.Image => dark ? Rgb(0xFF, 0x80, 0xAB) : Rgb(0xC2, 0x18, 0x5B),
        FileCategory.Video => dark ? Rgb(0xB3, 0x9D, 0xFF) : Rgb(0x5E, 0x35, 0xB1),
        FileCategory.Audio => dark ? Rgb(0x5C, 0xE1, 0xE6) : Rgb(0x00, 0x83, 0x8F),
        FileCategory.Document => dark ? Rgb(0x7A, 0xB8, 0xFF) : Rgb(0x15, 0x65, 0xC0),
        FileCategory.Spreadsheet => dark ? Rgb(0x7C, 0xD9, 0x92) : Rgb(0x2E, 0x7D, 0x32),
        FileCategory.Presentation => dark ? Rgb(0xFF, 0xAB, 0x66) : Rgb(0xD8, 0x43, 0x15),
        FileCategory.Pdf => dark ? Rgb(0xFF, 0x80, 0x80) : Rgb(0xC6, 0x28, 0x28),
        FileCategory.Archive => dark ? Rgb(0xE2, 0xB9, 0x8A) : Rgb(0x79, 0x55, 0x48),
        FileCategory.Code => dark ? Rgb(0x64, 0xE3, 0xC8) : Rgb(0x00, 0x79, 0x6B),
        FileCategory.Program => dark ? Rgb(0xC5, 0xE8, 0x6C) : Rgb(0x55, 0x8B, 0x2F),
        FileCategory.Font => dark ? Rgb(0xD0, 0xA9, 0xF5) : Rgb(0x7B, 0x1F, 0xA2),
        FileCategory.DiskImage => dark ? Rgb(0x9F, 0xB3, 0xC8) : Rgb(0x45, 0x5A, 0x64),
        FileCategory.Shortcut => dark ? Rgb(0x80, 0xD8, 0xFF) : Rgb(0x02, 0x77, 0xBD),
        _ => dark ? Rgb(0xE6, 0xE6, 0xE6) : Rgb(0x1F, 0x1F, 0x1F),
    };

    /// <summary>
    /// The brush for a name: the kind's colour, or the plain text colour of the theme. Never null, since a binding that
    /// sets a null foreground makes the text invisible.
    /// </summary>
    public static Brush NameBrush(FileCategory category, bool dark, bool colorful)
    {
        if (!colorful || category is FileCategory.Text or FileCategory.Other or FileCategory.Drive) category = FileCategory.Other;
        if (!Brushes.TryGetValue((category, dark), out SolidColorBrush? brush)) Brushes[(category, dark)] = brush = new SolidColorBrush(Of(category, dark));
        return brush;
    }

    /// <summary>The fill of a drive's usage bar: calm while there is room, amber when it gets full, red when nearly full.</summary>
    public static Color Usage(double used) => used switch
    {
        >= 0.92 => Rgb(0xEF, 0x44, 0x44),
        >= 0.80 => Rgb(0xF5, 0x9E, 0x0B),
        _ => Rgb(0x3B, 0x82, 0xF6),
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}

/// <summary>
/// Icons and thumbnails as image sources. Icons of ordinary files are shared by extension, so a folder of a thousand
/// text files needs one icon; programs, shortcuts and customised folders get their own. Thumbnails are kept for the
/// most recently shown few hundred files only.
/// </summary>
internal static class ImageCache
{
    private const int ThumbnailLimit = 600;

    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cpl", ".msc", ".appref-ms", ".scr", ".website", ".cur", ".ani",
    };

    private static readonly Dictionary<string, ImageSource> Icons = [];
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Thumbnails = [];
    private static readonly LinkedList<(string Key, ImageSource Image)> ThumbnailOrder = new();
    private static readonly Dictionary<string, List<Action<ImageSource?>>> Waiting = [];

    public static string IconKey(FileEntry entry, int pixels)
    {
        string what = entry.Kind switch
        {
            EntryKind.Drive => entry.FullPath,
            // Folders with the read-only or system attribute may have their own icon in desktop.ini.
            EntryKind.Folder => (entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0 ? entry.FullPath : "<folder>",
            _ => OwnIcon.Contains(entry.Extension) || entry.Extension.Length == 0 ? entry.FullPath : entry.Extension.ToLowerInvariant(),
        };
        return what + "|" + pixels;
    }

    /// <summary>Sets the item's icon, at once from the cache or later from the shell.</summary>
    public static void RequestIcon(FileItem item, int pixels, DispatcherQueue ui)
    {
        if (item.Icon is not null) return;
        string key = IconKey(item.Entry, pixels);
        if (Icons.TryGetValue(key, out ImageSource? cached))
        {
            item.Icon = cached;
            return;
        }
        // An icon shared by extension serves many rows, so it is fetched even when this row has scrolled away.
        bool shared = !key.StartsWith(item.Entry.FullPath + "|", StringComparison.Ordinal);
        Load(key, item.Entry.FullPath, pixels, thumbnail: false, shared ? () => true : () => item.IsRealized, ui, image =>
        {
            if (image is null) return;
            Icons[key] = image;
            item.Icon = image;
        });
    }

    public static void RequestThumbnail(FileItem item, int pixels, DispatcherQueue ui)
    {
        if (item.Thumbnail is not null || item.Entry.Kind == EntryKind.Drive) return;
        string key = $"{item.Entry.FullPath}|{item.Entry.ModifiedUtc.Ticks}|{pixels}";
        if (Thumbnails.TryGetValue(key, out var node))
        {
            ThumbnailOrder.Remove(node);
            ThumbnailOrder.AddFirst(node);
            item.Thumbnail = node.Value.Image;
            return;
        }
        Load(key, item.Entry.FullPath, pixels, thumbnail: true, () => item.IsRealized, ui, image =>
        {
            if (image is null) return;
            var added = ThumbnailOrder.AddFirst((key, image));
            Thumbnails[key] = added;
            while (ThumbnailOrder.Count > ThumbnailLimit)
            {
                Thumbnails.Remove(ThumbnailOrder.Last!.Value.Key);
                ThumbnailOrder.RemoveLast();
            }
            item.Thumbnail = image;
        });
    }

    /// <summary>A large thumbnail for the preview pane, not cached.</summary>
    public static void RequestLarge(string path, int pixels, DispatcherQueue ui, Func<bool> stillWanted, Action<ImageSource?> done)
    {
        ShellImages.Enqueue(path, pixels, thumbnail: true, stillWanted, pixels2 => ui.SafeEnqueue(() => done(pixels2 is null ? null : ToBitmap(pixels2))));
    }

    private static void Load(string key, string path, int pixels, bool thumbnail, Func<bool> stillWanted, DispatcherQueue ui, Action<ImageSource?> done)
    {
        // Rows that need the same image wait for one request.
        if (Waiting.TryGetValue(key, out var list))
        {
            list.Add(done);
            return;
        }
        Waiting[key] = [done];
        ShellImages.Enqueue(path, pixels, thumbnail, stillWanted, result => ui.SafeEnqueue(() =>
        {
            ImageSource? image = result is null ? null : ToBitmap(result);
            if (!Waiting.Remove(key, out var callbacks)) return;
            foreach (var callback in callbacks) callback(image);
        }));
    }

    public static WriteableBitmap ToBitmap(ImagePixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels.Bgra);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Drops cached thumbnails, for example after files were edited.</summary>
    public static void ClearThumbnails()
    {
        Thumbnails.Clear();
        ThumbnailOrder.Clear();
    }

    /// <summary>The shell's type name for an entry, cached by extension.</summary>
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    public static string TypeName(FileEntry entry)
    {
        if (entry.Kind == EntryKind.Drive) return Loc.Drive(entry.DriveType);
        string key = entry.Kind == EntryKind.Folder ? "<folder>" : entry.Extension.Length == 0 ? "<none>" : entry.Extension;
        if (!TypeNames.TryGetValue(key, out string? name))
        {
            name = ShellImages.TypeName(entry.Kind == EntryKind.Folder ? "folder" : "file" + entry.Extension, entry.Kind == EntryKind.Folder);
            if (string.IsNullOrEmpty(name)) name = entry.Extension.TrimStart('.').ToUpperInvariant();
            TypeNames[key] = name;
        }
        return name;
    }
}
