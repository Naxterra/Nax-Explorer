using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace NaxExplorer.Core;

/// <summary>Recycle Bin availability and maximum per-item capacity configured on a volume.</summary>
public sealed record RecycleBinVolumeInfo(bool HasRecycleBin, long? MaxCapacityBytes);

/// <summary>Why a delete requires Nax Explorer's permanent-delete confirmation dialog.</summary>
public enum PermanentDeleteReason
{
    UserRequested,
    NoRecycleBin,
    TooLargeForRecycleBin,
}

/// <summary>How a delete request splits between the Recycle Bin and direct permanent deletion.</summary>
public sealed record DeletePlan(
    IReadOnlyList<string> RecyclePaths,
    IReadOnlyList<string> PermanentPaths,
    PermanentDeleteReason? ConfirmationReason);

/// <summary>
/// Queries Windows Recycle Bin availability and per-volume quota so deletes to the Recycle Bin stay safe and undoable,
/// while items on volumes without a Recycle Bin or items exceeding the volume's Recycle Bin capacity can be confirmed
/// in Nax Explorer and deleted through <see cref="FastPermanentDelete"/> instead of slow single-threaded COM deletion.
/// </summary>
public static class RecycleBins
{
    private const string BitBucketVolumeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";
    private const string ExplorerPoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const int GetFileExInfoStandard = 0;

    /// <summary>
    /// True when the volume of <paramref name="path"/> has a Recycle Bin enabled, so a delete there can be undone. A
    /// network path, a path without a drive, a drive configured with NukeOnDelete, or a drive Windows answers with an
    /// error counts as "no Recycle Bin", so the app asks before deleting for good rather than assume an undo.
    /// </summary>
    public static bool HasRecycleBin(string path) => GetVolumeInfo(path).HasRecycleBin;

    /// <summary>
    /// Reads whether <paramref name="path"/>'s volume has a Recycle Bin and its configured maximum capacity in bytes
    /// (from Windows' BitBucket registry settings), or null capacity when no explicit limit is stored.
    /// </summary>
    public static RecycleBinVolumeInfo GetVolumeInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || PathTools.IsShellPlace(path) || CompressedFolders.TrySplit(path, out _, out _))
            return new RecycleBinVolumeInfo(false, null);
        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return new RecycleBinVolumeInfo(false, null);
        }
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return new RecycleBinVolumeInfo(false, null);
        if (IsNoRecyclePolicyActive())
            return new RecycleBinVolumeInfo(false, null);
        string mountPoint = ResolveVolumeMountPoint(path, root);
        var info = new QueryInfo { Size = QueryInfoSize };
        if (SHQueryRecycleBin(mountPoint, ref info) != 0)
            return new RecycleBinVolumeInfo(false, null);
        string? guid = TryGetVolumeGuid(mountPoint);
        if (guid is null)
            return new RecycleBinVolumeInfo(true, null);
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{BitBucketVolumeKey}\{guid}");
            if (key is null) return new RecycleBinVolumeInfo(true, null);
            return ReadBitBucketPolicy(key.GetValue("NukeOnDelete"), key.GetValue("MaxCapacity"), noRecyclePolicy: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return new RecycleBinVolumeInfo(true, null);
        }
    }

    /// <summary>
    /// Plans how <paramref name="paths"/> should be deleted. Normal deletes check each item's volume Recycle Bin
    /// policy and run a bounded metadata-only size check (never following links or hydrating cloud placeholders) so
    /// items larger than the volume's Recycle Bin quota can prompt in Nax Explorer and use fast permanent deletion.
    /// </summary>
    public static DeletePlan PlanDelete(
        IReadOnlyList<string> paths,
        bool permanentlyRequested,
        Func<string, bool>? isCloudPath,
        CancellationToken token,
        Func<string, RecycleBinVolumeInfo>? volumeInfoProvider = null,
        TimeSpan? sizeCheckBudget = null)
    {
        if (paths.Count == 0) return new DeletePlan([], [], null);
        if (permanentlyRequested || paths.Any(PathTools.IsShellPlace))
            return new DeletePlan([], paths, PermanentDeleteReason.UserRequested);

        var volumeInfos = new RecycleBinVolumeInfo[paths.Count];
        Dictionary<string, RecycleBinVolumeInfo>? mountCache = volumeInfoProvider is null ? new(StringComparer.OrdinalIgnoreCase) : null;
        for (int i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string path = paths[i];
            RecycleBinVolumeInfo info;
            if (volumeInfoProvider is not null)
            {
                info = volumeInfoProvider(path);
            }
            else
            {
                string? root = TryGetRoot(path);
                string key = root is not null && !root.StartsWith(@"\\", StringComparison.Ordinal)
                    ? ResolveVolumeMountPoint(path, root)
                    : path;
                if (!mountCache!.TryGetValue(key, out info!))
                {
                    info = GetVolumeInfo(path);
                    mountCache[key] = info;
                }
            }
            if (!info.HasRecycleBin)
                return new DeletePlan([], paths, PermanentDeleteReason.NoRecycleBin);
            volumeInfos[i] = info;
        }

        var recycle = new List<string>(paths.Count);
        var permanent = new List<string>();
        TimeSpan budget = sizeCheckBudget ?? TimeSpan.FromSeconds(10);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (budget > TimeSpan.Zero) budgetCts.CancelAfter(budget);

        bool budgetExpired = false;
        for (int i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string path = paths[i];
            if (budgetExpired || volumeInfos[i].MaxCapacityBytes is not long maxBytes || maxBytes <= 0)
            {
                recycle.Add(path);
                continue;
            }
            try
            {
                if (ExceedsCapacity(path, maxBytes, isCloudPath, budgetCts.Token))
                    permanent.Add(path);
                else
                    recycle.Add(path);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Time budget reached without proving the item exceeds Recycle Bin capacity; let Windows handle it.
                budgetExpired = true;
                recycle.Add(path);
            }
        }

        if (permanent.Count == 0)
            return new DeletePlan(paths, [], null);
        return new DeletePlan(recycle, permanent, PermanentDeleteReason.TooLargeForRecycleBin);
    }

    /// <summary>
    /// Returns true as soon as the cumulative metadata size of <paramref name="path"/> strictly exceeds
    /// <paramref name="maxCapacityBytes"/>. Never opens files, never enters cloud roots or unfetched cloud folders,
    /// never counts online-only cloud placeholders, and never follows symbolic links, junctions, or reparse points.
    /// </summary>
    public static bool ExceedsCapacity(string path, long maxCapacityBytes, Func<string, bool>? isCloudPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (maxCapacityBytes <= 0
            || string.IsNullOrWhiteSpace(path)
            || PathTools.IsShellPlace(path)
            || CompressedFolders.TrySplit(path, out _, out _)
            || FolderStats.IsNetworkPath(path)
            || FastPermanentDelete.HasTrailingDotOrSpaceSegment(path)
            || isCloudPath?.Invoke(path) == true)
            return false;

        if (!FastPermanentDelete.TryToExtendedPath(path, out string extPath))
            return false;

        if (!GetFileAttributesExW(extPath, GetFileExInfoStandard, out WIN32_FILE_ATTRIBUTE_DATA rootData))
            return false;

        FileAttributes rootAttrs = (FileAttributes)rootData.dwFileAttributes;
        if ((rootAttrs & FileAttributes.ReparsePoint) != 0
            || CloudFiles.IsOnlineOnly(rootAttrs)
            || CloudFiles.IsUnfetchedFolder(rootAttrs))
            return false;

        if ((rootAttrs & FileAttributes.Directory) == 0)
        {
            long fileSize = ((long)rootData.nFileSizeHigh << 32) | rootData.nFileSizeLow;
            return fileSize > maxCapacityBytes;
        }

        var pending = new Stack<string>();
        pending.Push(extPath);
        long total = 0;
        int checkedEntries = 0;
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entries = new FileSystemEnumerable<(string ExtPath, bool IsDirectory, long Length, FileAttributes Attrs)>(
                    folder,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length, entry.Attributes),
                    options);
                foreach (var entry in entries)
                {
                    if ((++checkedEntries & 0xFF) == 0) token.ThrowIfCancellationRequested();
                    if ((entry.Attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry.IsDirectory)
                    {
                        if (CloudFiles.IsUnfetchedFolder(entry.Attrs)) continue;
                        string normalChild = FastPermanentDelete.StripExtendedPrefix(entry.ExtPath);
                        if (isCloudPath?.Invoke(normalChild) == true) continue;
                        pending.Push(entry.ExtPath);
                    }
                    else
                    {
                        if (CloudFiles.IsOnlineOnly(entry.Attrs)) continue;
                        if (entry.Length > maxCapacityBytes - total) return true;
                        total += entry.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
            }
        }
        return false;
    }

    internal static RecycleBinVolumeInfo ReadBitBucketPolicy(object? nukeOnDeleteValue, object? maxCapacityMbValue, bool noRecyclePolicy)
    {
        if (noRecyclePolicy) return new RecycleBinVolumeInfo(false, null);
        if (TryReadDword(nukeOnDeleteValue, out uint nuke) && nuke != 0)
            return new RecycleBinVolumeInfo(false, null);
        if (TryReadDword(maxCapacityMbValue, out uint maxMb) && maxMb > 0)
            return new RecycleBinVolumeInfo(true, (long)maxMb * 1024L * 1024L);
        return new RecycleBinVolumeInfo(true, null);
    }

    internal static string? ParseVolumeGuid(string? volumeName)
    {
        if (string.IsNullOrWhiteSpace(volumeName)) return null;
        int open = volumeName.IndexOf('{');
        int close = open >= 0 ? volumeName.IndexOf('}', open + 1) : -1;
        if (open < 0 || close <= open + 1) return null;
        return volumeName[open..(close + 1)];
    }

    private static bool TryReadDword(object? value, out uint result)
    {
        if (value is int i)
        {
            result = unchecked((uint)i);
            return true;
        }
        if (value is long l && l >= 0 && l <= uint.MaxValue)
        {
            result = (uint)l;
            return true;
        }
        result = 0;
        return false;
    }

    private static bool IsNoRecyclePolicyActive()
    {
        try
        {
            using RegistryKey? user = Registry.CurrentUser.OpenSubKey(ExplorerPoliciesKey);
            if (TryReadDword(user?.GetValue("NoRecycleFiles"), out uint u) && u != 0) return true;
            using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(ExplorerPoliciesKey);
            if (TryReadDword(machine?.GetValue("NoRecycleFiles"), out uint m) && m != 0) return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return false;
    }

    private static string? TryGetRoot(string path)
    {
        try { return Path.GetPathRoot(path); }
        catch (ArgumentException) { return null; }
    }

    private static string ResolveVolumeMountPoint(string path, string fallbackRoot)
    {
        // Trailing-dot/space paths must never be handed to Win32 path-normalizing APIs; use the drive root directly.
        if (FastPermanentDelete.HasTrailingDotOrSpaceSegment(path))
            return EnsureTrailingBackslash(fallbackRoot);
        char[] buffer = new char[Math.Max(260, path.Length + 16)];
        if (GetVolumePathNameW(path, buffer, (uint)buffer.Length))
        {
            int len = Array.IndexOf(buffer, '\0');
            string mount = len >= 0 ? new string(buffer, 0, len) : new string(buffer);
            if (!string.IsNullOrWhiteSpace(mount)) return EnsureTrailingBackslash(mount);
        }
        return EnsureTrailingBackslash(fallbackRoot);
    }

    private static string? TryGetVolumeGuid(string mountPoint)
    {
        char[] buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(EnsureTrailingBackslash(mountPoint), buffer, (uint)buffer.Length))
            return null;
        int len = Array.IndexOf(buffer, '\0');
        string volumeName = len >= 0 ? new string(buffer, 0, len) : new string(buffer);
        return ParseVolumeGuid(volumeName);
    }

    private static string EnsureTrailingBackslash(string path) =>
        path.EndsWith('\\') ? path : path + '\\';

    /// <summary>The size Windows expects for its query: 24 bytes on 64-bit Windows, where the struct is not packed.</summary>
    public static int QueryInfoSize => Marshal.SizeOf<QueryInfo>();

    /// <summary>SHQUERYRBINFO. shellapi.h packs it tightly only for 32-bit builds; the app is 64-bit, so it keeps natural alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref QueryInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string lpszFileName, [Out] char[] lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileAttributesExW(string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);
}
