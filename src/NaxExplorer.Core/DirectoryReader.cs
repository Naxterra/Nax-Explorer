using System.IO.Enumeration;

namespace NaxExplorer.Core;

/// <summary>What a folder listing includes.</summary>
public sealed record ListingOptions(bool ShowHidden, bool ShowProtectedSystemFiles = false);

/// <summary>Reads folders and drives quickly, without opening any file.</summary>
public static class DirectoryReader
{
    /// <summary>
    /// Lists one folder. Hidden items appear only when <see cref="ListingOptions.ShowHidden"/> is set, and items that are
    /// both hidden and system (such as desktop.ini and $RECYCLE.BIN) only when protected system files are shown too, as
    /// in Windows Explorer. Throws when the folder itself cannot be read, so the caller can say why.
    /// </summary>
    public static List<FileEntry> Read(string folder, ListingOptions options, CancellationToken token = default)
    {
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };
        var enumerable = new FileSystemEnumerable<FileEntry>(folder, ToEntry, enumerationOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => IsVisible(entry.Attributes, options),
        };
        var result = new List<FileEntry>();
        foreach (FileEntry entry in enumerable)
        {
            if ((result.Count & 255) == 0) token.ThrowIfCancellationRequested();
            result.Add(entry);
        }
        return result;
    }

    public static bool IsVisible(FileAttributes attributes, ListingOptions options)
    {
        bool hidden = (attributes & FileAttributes.Hidden) != 0;
        if (!hidden) return true;
        if (!options.ShowHidden) return false;
        bool system = (attributes & FileAttributes.System) != 0;
        return !system || options.ShowProtectedSystemFiles;
    }

    public static FileEntry ToEntry(ref FileSystemEntry entry)
    {
        bool folder = entry.IsDirectory;
        return new FileEntry
        {
            Name = entry.FileName.ToString(),
            FullPath = entry.ToFullPath(),
            Kind = folder ? EntryKind.Folder : EntryKind.File,
            Size = folder ? 0 : entry.Length,
            ModifiedUtc = entry.LastWriteTimeUtc.UtcDateTime,
            CreatedUtc = entry.CreationTimeUtc.UtcDateTime,
            Attributes = entry.Attributes,
        };
    }

    /// <summary>
    /// Lists the drives for "This PC". A drive that is not ready, such as an empty card reader, is listed without sizes.
    /// Call it off the UI thread: a disconnected network drive can take seconds to answer.
    /// </summary>
    public static List<FileEntry> ReadDrives()
    {
        var result = new List<FileEntry>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            long total = 0, free = 0;
            string label = "";
            try
            {
                if (drive.IsReady)
                {
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                    label = drive.VolumeLabel;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The drive is listed without sizes.
            }
            result.Add(new FileEntry
            {
                Name = drive.Name.TrimEnd('\\'),
                FullPath = drive.Name,
                Kind = EntryKind.Drive,
                Size = total,
                FreeBytes = free,
                Label = label,
                DriveType = drive.DriveType,
            });
        }
        return result;
    }

    /// <summary>Reads one item again, or returns null when it no longer exists.</summary>
    public static FileEntry? ReadOne(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return new FileEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    Kind = EntryKind.File,
                    Size = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    CreatedUtc = info.CreationTimeUtc,
                    Attributes = info.Attributes,
                };
            }
            var folder = new DirectoryInfo(path);
            if (folder.Exists)
            {
                return new FileEntry
                {
                    Name = folder.Name,
                    FullPath = folder.FullName,
                    Kind = folder.Parent is null ? EntryKind.Drive : EntryKind.Folder,
                    ModifiedUtc = folder.LastWriteTimeUtc,
                    CreatedUtc = folder.CreationTimeUtc,
                    Attributes = folder.Attributes,
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }
}
