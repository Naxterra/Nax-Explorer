using System.Diagnostics;
using System.IO.Enumeration;

namespace NaxExplorer.Core;

/// <summary>
/// What lies below a set of folders and files: how many folders and files, and their size in bytes; and how many of the
/// files live only in the cloud, with their size.
/// </summary>
public sealed record FolderTotals(long Folders, long Files, long Bytes, bool Complete, long OnlineFiles = 0, long OnlineBytes = 0, long UnfetchedFolders = 0);

/// <summary>Counts folders and files and adds up their sizes, for the details panel.</summary>
public static class FolderStats
{
    /// <summary>How often the totals so far are reported while a large folder is counted.</summary>
    private const int ReportMilliseconds = 250;

    /// <summary>
    /// Counts every folder and file below the given folders, and the given files themselves, including hidden and
    /// system items as Explorer's Properties does. Junctions and links are counted but not followed, so a loop or a
    /// link to another drive never inflates the totals. Only names and sizes are read, so cloud files that live only
    /// online count with their full size and are never downloaded; cloud folders below the given paths whose list of
    /// files is not on this device are counted but not entered (see <see cref="Enters"/>), and the totals say how many.
    /// With <paramref name="skipUnfetchedRoots"/>, as for selected items, a given cloud folder whose list is not on this
    /// device is not looked into either; without it, as for the folder the user is in, whose list is fetched already,
    /// the given folders are always looked into. <paramref name="progress"/> receives the totals so
    /// far about four times a second; the call ends early with an exception when <paramref name="token"/> is cancelled.
    /// </summary>
    public static FolderTotals Measure(IEnumerable<string> paths, CancellationToken token, Action<FolderTotals>? progress = null, bool skipUnfetchedRoots = false)
    {
        long folders = 0, files = 0, bytes = 0, onlineFiles = 0, onlineBytes = 0, unfetched = 0;
        var watch = Stopwatch.StartNew();
        long nextReport = ReportMilliseconds;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }
            if (skipUnfetchedRoots && CloudFiles.IsUnfetchedFolder(attributes))
            {
                // A selected cloud folder whose list is not on this device, judged by its attributes now, not when the
                // list was read; it is counted as one folder whose contents are not loaded.
                folders++;
                unfetched++;
                continue;
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                long size = SizeOf(path);
                files++;
                bytes += size;
                if (CloudFiles.IsOnlineOnly(attributes))
                {
                    onlineFiles++;
                    onlineBytes += size;
                }
                continue;
            }
            var walk = new FileSystemEnumerable<(long Size, bool Online, bool Unfetched)>(
                path,
                (ref FileSystemEntry entry) => entry.IsDirectory
                    ? (-1, false, CloudFiles.IsUnfetchedFolder(entry.Attributes))
                    : (entry.Length, CloudFiles.IsOnlineOnly(entry.Attributes), false),
                options)
            {
                ShouldRecursePredicate = (ref FileSystemEntry entry) => Enters(entry.Attributes),
            };
            try
            {
                foreach ((long size, bool online, bool notFetched) in walk)
                {
                    token.ThrowIfCancellationRequested();
                    if (size < 0)
                    {
                        folders++;
                        if (notFetched) unfetched++;
                    }
                    else
                    {
                        files++;
                        bytes += size;
                        if (online)
                        {
                            onlineFiles++;
                            onlineBytes += size;
                        }
                    }
                    if (progress is not null && watch.ElapsedMilliseconds >= nextReport)
                    {
                        progress(new FolderTotals(folders, files, bytes, Complete: false, onlineFiles, onlineBytes, unfetched));
                        nextReport = watch.ElapsedMilliseconds + ReportMilliseconds;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder vanished or became unreadable while it was counted; what was counted stays.
            }
        }
        return new FolderTotals(folders, files, bytes, Complete: true, onlineFiles, onlineBytes, unfetched);
    }

    /// <summary>
    /// True for a folder the count walks into. Junctions and links are not followed, and neither are cloud folders
    /// whose list of files is not on this device yet, because looking into them would make the sync app fetch it.
    /// </summary>
    public static bool Enters(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | CloudFiles.RecallOnOpen | CloudFiles.RecallOnDataAccess)) == 0;

    /// <summary>
    /// True for a folder on another computer, reached through a share or a network drive. Counting it can keep the
    /// network busy for minutes, so the details panel counts it only when asked.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        // The long forms \\?\UNC\server\share and \\.\UNC\server\share are shares too; \\?\C:\... and \\.\C:\... name a local drive.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) path = path[4..];
        else if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
