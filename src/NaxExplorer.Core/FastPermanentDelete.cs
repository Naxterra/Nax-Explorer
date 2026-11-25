using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;

namespace NaxExplorer.Core;

/// <summary>One item that fast permanent deletion could not remove directly.</summary>
public sealed record FastDeleteFailure(string Path, string Name, int Win32Error);

/// <summary>Result of a direct Win32 permanent deletion pass.</summary>
public sealed record FastDeleteResult(
    List<string> FallbackToShell,
    List<FastDeleteFailure> Failures,
    List<string> DeferredParents,
    bool Cancelled);

/// <summary>
/// High-throughput permanent deletion for local filesystem trees after the user confirmed Shift+Delete (or permanent
/// deletion on a drive without a Recycle Bin). Uses exact extended-length Win32 paths (without stripping trailing dots
/// or spaces), iterative post-order traversal, and bounded parallel leaf file deletion while preserving every safety
/// invariant:
/// - Never follows symbolic links or junctions (checks exact NTFS reparse tags and removes only the link entry).
/// - Never opens or hydrates cloud placeholders (hands cloud roots, nested cloud sync roots, online-only files,
///   unfetched folders, and any non-link or unreadable reparse points back to Windows IFileOperation).
/// - Defers access-denied items to Windows IFileOperation so UAC elevation prompts still work normally.
/// - Restores original ReadOnly attributes whenever deletion of an item fails.
/// </summary>
public static class FastPermanentDelete
{
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint IoReparseTagSymlink = 0xA000000C;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorNotSupported = 50;
    private const int ErrorDirNotEmpty = 145;
    private const int ErrorPrivilegeNotHeld = 1314;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private sealed class DirFrame(string extPath, string normalPath, FileAttributes attributes, DirFrame? parent)
    {
        public string ExtPath { get; } = extPath;
        public string NormalPath { get; } = normalPath;
        public FileAttributes Attributes { get; } = attributes;
        public DirFrame? Parent { get; } = parent;
        public bool Expanded { get; set; }
        public bool HasFallbackChild { get; set; }
        public bool HasHardFailure { get; set; }
    }

    public static FastDeleteResult Delete(
        IReadOnlyList<string> paths,
        Func<string, bool>? isCloudPath,
        Func<bool> isCancelled,
        Action<string>? onProgress = null)
    {
        var fallback = new List<string>();
        var failures = new List<FastDeleteFailure>();
        var deferredParents = new List<string>();
        long lastProgress = 0;

        void Report(string displayPath)
        {
            if (onProgress is null) return;
            long now = Environment.TickCount64;
            long prev = Volatile.Read(ref lastProgress);
            if (prev != 0 && now - prev < 100) return;
            if (Interlocked.CompareExchange(ref lastProgress, now, prev) == prev)
                onProgress(DisplayName(displayPath));
        }

        foreach (string rawPath in paths)
        {
            if (isCancelled()) return new FastDeleteResult(fallback, failures, deferredParents, Cancelled: true);
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            if (PathTools.IsShellPlace(rawPath) || CompressedFolders.TrySplit(rawPath, out _, out _) || isCloudPath?.Invoke(rawPath) == true)
            {
                if (!PathTools.IsShellPlace(rawPath) && HasTrailingDotOrSpaceSegment(rawPath))
                    failures.Add(new FastDeleteFailure(rawPath, DisplayName(rawPath), ErrorNotSupported));
                else
                    fallback.Add(rawPath);
                continue;
            }
            if (!TryToExtendedPath(rawPath, out string extPath))
            {
                DeferOrFail(rawPath, ErrorNotSupported, fallback, failures);
                continue;
            }

            uint rawAttrs = GetFileAttributesW(extPath);
            if (rawAttrs == InvalidFileAttributes)
            {
                int err = Marshal.GetLastWin32Error();
                if (err is ErrorFileNotFound or ErrorPathNotFound) continue;
                if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld) DeferOrFail(rawPath, err, fallback, failures);
                else failures.Add(new FastDeleteFailure(rawPath, DisplayName(rawPath), err));
                continue;
            }

            FileAttributes attrs = (FileAttributes)rawAttrs;
            if (ShouldDeferToShell(extPath, attrs, out bool isLink))
            {
                DeferOrFail(rawPath, ErrorNotSupported, fallback, failures);
                continue;
            }

            if ((attrs & FileAttributes.Directory) == 0)
            {
                Report(rawPath);
                int err = DeleteFileFast(extPath, attrs);
                if (err is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
                if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld) DeferOrFail(rawPath, err, fallback, failures);
                else failures.Add(new FastDeleteFailure(rawPath, DisplayName(rawPath), err));
                continue;
            }

            if (isLink)
            {
                Report(rawPath);
                int err = RemoveDirectoryFast(extPath, attrs);
                if (err is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
                if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld) DeferOrFail(rawPath, err, fallback, failures);
                else failures.Add(new FastDeleteFailure(rawPath, DisplayName(rawPath), err));
                continue;
            }

            DeleteDirectoryTree(extPath, rawPath, attrs, isCloudPath, fallback, failures, deferredParents, isCancelled, Report);
        }

        return new FastDeleteResult(fallback, failures, deferredParents, Cancelled: isCancelled());
    }

    /// <summary>
    /// Attempts to remove directories left behind solely because nested items were deferred to IFileOperation (such as
    /// elevated or cloud children) after that fallback completes. Returns any empty directories that still require shell
    /// elevation or failed with a real Win32 error, while refusing to hand non-empty directories or trailing-dot/space
    /// paths back to recursive shell deletion.
    /// </summary>
    public static (List<string> NeedsElevation, List<FastDeleteFailure> Failures) TryRemoveEmptyDirectories(
        IReadOnlyList<string> deferredParents,
        bool priorFailuresExist)
    {
        var needsElevation = new List<string>();
        var pendingElevation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<FastDeleteFailure>();
        foreach (string path in deferredParents)
        {
            if (!TryToExtendedPath(path, out string extPath))
            {
                if (HasTrailingDotOrSpaceSegment(path))
                    failures.Add(new FastDeleteFailure(path, DisplayName(path), ErrorNotSupported));
                else
                    PromoteToElevation(path, needsElevation, pendingElevation);
                continue;
            }
            uint rawAttrs = GetFileAttributesW(extPath);
            if (rawAttrs == InvalidFileAttributes) continue;
            FileAttributes attrs = (FileAttributes)rawAttrs;
            if ((attrs & FileAttributes.Directory) == 0) continue;
            int err = RemoveDirectoryFast(extPath, attrs);
            if (err is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
            if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld)
            {
                if (HasTrailingDotOrSpaceSegment(path))
                {
                    failures.Add(new FastDeleteFailure(path, DisplayName(path), err));
                }
                else if (HasAnyChildOutside(extPath, path, pendingElevation))
                {
                    if (!priorFailuresExist)
                    {
                        failures.Add(new FastDeleteFailure(path, DisplayName(path), ErrorDirNotEmpty));
                    }
                }
                else
                {
                    PromoteToElevation(path, needsElevation, pendingElevation);
                }
            }
            else if (err == ErrorDirNotEmpty)
            {
                if (!HasTrailingDotOrSpaceSegment(path) && pendingElevation.Count > 0 && !HasAnyChildOutside(extPath, path, pendingElevation))
                {
                    PromoteToElevation(path, needsElevation, pendingElevation);
                }
                else if (!priorFailuresExist)
                {
                    failures.Add(new FastDeleteFailure(path, DisplayName(path), err));
                }
            }
            else
            {
                failures.Add(new FastDeleteFailure(path, DisplayName(path), err));
            }
        }
        return (needsElevation, failures);
    }

    private static void PromoteToElevation(string dirPath, List<string> needsElevation, HashSet<string> pendingElevation)
    {
        needsElevation.RemoveAll(existing => PathTools.IsSameOrInside(existing, dirPath));
        needsElevation.Add(dirPath);
        pendingElevation.Add(dirPath.TrimEnd('\\'));
    }

    private static void DeferOrFail(
        string path,
        int win32Error,
        List<string> fallback,
        List<FastDeleteFailure> failures,
        DirFrame? parentFrame = null)
    {
        // Paths with trailing dots or spaces must never reach SHParseDisplayName because Win32 shell normalization
        // strips those characters and could target a sibling item.
        if (HasTrailingDotOrSpaceSegment(path))
        {
            failures.Add(new FastDeleteFailure(path, DisplayName(path), win32Error != 0 ? win32Error : ErrorAccessDenied));
            if (parentFrame is not null) parentFrame.HasHardFailure = true;
            return;
        }
        fallback.Add(path);
        if (parentFrame is not null) parentFrame.HasFallbackChild = true;
    }

    private static void DeleteDirectoryTree(
        string rootExtPath,
        string rootNormalPath,
        FileAttributes rootAttrs,
        Func<string, bool>? isCloudPath,
        List<string> fallback,
        List<FastDeleteFailure> failures,
        List<string> deferredParents,
        Func<bool> isCancelled,
        Action<string> report)
    {
        var stack = new Stack<DirFrame>();
        stack.Push(new DirFrame(rootExtPath, rootNormalPath, rootAttrs, parent: null));

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };

        while (stack.Count > 0)
        {
            if (isCancelled()) return;
            DirFrame frame = stack.Pop();

            if (!frame.Expanded)
            {
                var files = new List<(string ExtPath, string NormalPath, FileAttributes Attrs)>();
                var subdirs = new List<(string ExtPath, string NormalPath, FileAttributes Attrs)>();
                bool enumFailed = false;
                try
                {
                    var entries = new FileSystemEnumerable<(string ExtPath, FileAttributes Attrs, bool IsDir)>(
                        frame.ExtPath,
                        (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes, entry.IsDirectory),
                        enumOptions);
                    foreach ((string childExtPath, FileAttributes attrs, bool isDir) in entries)
                    {
                        if (isCancelled()) return;
                        string childNormalPath = StripExtendedPrefix(childExtPath);
                        if (isCloudPath?.Invoke(childNormalPath) == true || ShouldDeferToShell(childExtPath, attrs, out bool isLink))
                        {
                            DeferOrFail(childNormalPath, ErrorNotSupported, fallback, failures, frame);
                            continue;
                        }
                        if (isDir)
                        {
                            if (isLink)
                            {
                                report(childNormalPath);
                                int linkErr = RemoveDirectoryFast(childExtPath, attrs);
                                if (linkErr is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
                                if (linkErr is ErrorAccessDenied or ErrorPrivilegeNotHeld)
                                {
                                    DeferOrFail(childNormalPath, linkErr, fallback, failures, frame);
                                }
                                else
                                {
                                    failures.Add(new FastDeleteFailure(childNormalPath, DisplayName(childNormalPath), linkErr));
                                    frame.HasHardFailure = true;
                                }
                                continue;
                            }
                            subdirs.Add((childExtPath, childNormalPath, attrs));
                        }
                        else
                        {
                            files.Add((childExtPath, childNormalPath, attrs));
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
                {
                    DeferOrFail(frame.NormalPath, ErrorAccessDenied, fallback, failures, frame.Parent);
                    enumFailed = true;
                }
                catch (DirectoryNotFoundException)
                {
                    enumFailed = true;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    int err = ex.HResult & 0xFFFF;
                    if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld)
                    {
                        DeferOrFail(frame.NormalPath, err, fallback, failures, frame.Parent);
                    }
                    else if (err is not (ErrorFileNotFound or ErrorPathNotFound))
                    {
                        failures.Add(new FastDeleteFailure(frame.NormalPath, DisplayName(frame.NormalPath), err != 0 ? err : ErrorAccessDenied));
                        if (frame.Parent is not null) frame.Parent.HasHardFailure = true;
                    }
                    enumFailed = true;
                }

                if (enumFailed) continue;

                if (files.Count >= 32)
                {
                    var gate = new Lock();
                    Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, (i, loopState) =>
                    {
                        if (isCancelled())
                        {
                            loopState.Stop();
                            return;
                        }
                        var (extFile, normalFile, fileAttrs) = files[i];
                        report(normalFile);
                        int err = DeleteFileFast(extFile, fileAttrs);
                        if (err is 0 or ErrorFileNotFound or ErrorPathNotFound) return;
                        lock (gate)
                        {
                            if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld)
                            {
                                DeferOrFail(normalFile, err, fallback, failures, frame);
                            }
                            else
                            {
                                failures.Add(new FastDeleteFailure(normalFile, DisplayName(normalFile), err));
                                frame.HasHardFailure = true;
                            }
                        }
                    });
                }
                else
                {
                    foreach (var (extFile, normalFile, fileAttrs) in files)
                    {
                        if (isCancelled()) return;
                        report(normalFile);
                        int err = DeleteFileFast(extFile, fileAttrs);
                        if (err is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
                        if (err is ErrorAccessDenied or ErrorPrivilegeNotHeld)
                        {
                            DeferOrFail(normalFile, err, fallback, failures, frame);
                        }
                        else
                        {
                            failures.Add(new FastDeleteFailure(normalFile, DisplayName(normalFile), err));
                            frame.HasHardFailure = true;
                        }
                    }
                }

                if (isCancelled()) return;
                frame.Expanded = true;
                stack.Push(frame);
                for (int i = subdirs.Count - 1; i >= 0; i--)
                {
                    stack.Push(new DirFrame(subdirs[i].ExtPath, subdirs[i].NormalPath, subdirs[i].Attrs, frame));
                }
                continue;
            }

            // Post-order: all child files and subdirectories have been processed.
            if (frame.HasHardFailure)
            {
                if (frame.Parent is not null) frame.Parent.HasHardFailure = true;
                continue;
            }
            if (frame.HasFallbackChild)
            {
                deferredParents.Add(frame.NormalPath);
                if (frame.Parent is not null) frame.Parent.HasFallbackChild = true;
                continue;
            }

            report(frame.NormalPath);
            int dirErr = RemoveDirectoryFast(frame.ExtPath, frame.Attributes);
            if (dirErr is 0 or ErrorFileNotFound or ErrorPathNotFound) continue;
            if (dirErr is ErrorAccessDenied or ErrorPrivilegeNotHeld or ErrorDirNotEmpty)
            {
                DeferOrFail(frame.NormalPath, dirErr, fallback, failures, frame.Parent);
            }
            else
            {
                failures.Add(new FastDeleteFailure(frame.NormalPath, DisplayName(frame.NormalPath), dirErr));
                if (frame.Parent is not null) frame.Parent.HasHardFailure = true;
            }
        }
    }

    private static bool ShouldDeferToShell(string extPath, FileAttributes attrs, out bool isLink)
    {
        isLink = false;
        if (CloudFiles.IsOnlineOnly(attrs) || CloudFiles.IsUnfetchedFolder(attrs)) return true;
        if ((attrs & FileAttributes.ReparsePoint) == 0) return false;
        // Inspect the exact NTFS reparse tag; only genuine symlinks and junctions are deleted directly.
        // Any cloud placeholder, AppExecLink, filter-managed, or unreadable reparse point defers to IFileOperation.
        if (!TryReadReparseTag(extPath, out uint tag)) return true;
        if (tag is IoReparseTagSymlink or IoReparseTagMountPoint)
        {
            isLink = true;
            return false;
        }
        return true;
    }

    private static bool TryReadReparseTag(string extPath, out uint tag)
    {
        tag = 0;
        string trimmed = extPath.TrimEnd('\\');
        IntPtr handle = FindFirstFileW(trimmed, out WIN32_FIND_DATAW data);
        if (handle == InvalidHandleValue) return false;
        FindClose(handle);
        tag = data.dwReserved0;
        return true;
    }

    private static int DeleteFileFast(string extPath, FileAttributes attrs)
    {
        bool clearedReadOnly = false;
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            uint next = (uint)(attrs & ~FileAttributes.ReadOnly);
            if (next == 0) next = FileAttributeNormal;
            clearedReadOnly = SetFileAttributesW(extPath, next);
        }
        if (DeleteFileW(extPath)) return 0;
        int err = Marshal.GetLastWin32Error();
        if (clearedReadOnly && err is not (ErrorFileNotFound or ErrorPathNotFound))
            _ = SetFileAttributesW(extPath, (uint)attrs);
        return err;
    }

    private static int RemoveDirectoryFast(string extPath, FileAttributes attrs)
    {
        bool clearedReadOnly = false;
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            uint next = (uint)(attrs & ~FileAttributes.ReadOnly);
            if (next == 0) next = FileAttributeNormal;
            clearedReadOnly = SetFileAttributesW(extPath, next);
        }
        if (RemoveDirectoryW(extPath)) return 0;
        int err = Marshal.GetLastWin32Error();
        if (clearedReadOnly && err is not (ErrorFileNotFound or ErrorPathNotFound))
            _ = SetFileAttributesW(extPath, (uint)attrs);
        return err;
    }

    /// <summary>
    /// Converts an already-rooted filesystem path to a Win32 extended-length path without invoking Path.GetFullPath,
    /// preserving exact trailing dots and spaces so a sibling item can never be targeted by mistake.
    /// </summary>
    internal static bool TryToExtendedPath(string path, out string extPath)
    {
        extPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            extPath = path;
            return true;
        }
        string normalized = path.IndexOf('/') >= 0 ? path.Replace('/', '\\') : path;
        if (HasRelativeSegments(normalized)) return false;
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (normalized.Length <= 2 || normalized[2] == '\\') return false;
            extPath = @"\\?\UNC\" + normalized[2..];
            return true;
        }
        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            extPath = @"\\?\" + normalized;
            return true;
        }
        return false;
    }

    private static bool HasRelativeSegments(string path)
    {
        ReadOnlySpan<char> span = path.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && span[i] == '\\') i++;
            int start = i;
            while (i < span.Length && span[i] != '\\') i++;
            ReadOnlySpan<char> segment = span[start..i];
            if (segment.SequenceEqual(".") || segment.SequenceEqual("..")) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if any segment after the drive or UNC share root ends with a dot or space. Win32 shell APIs strip
    /// those trailing characters and could resolve the path to an unrelated sibling item.
    /// </summary>
    public static bool HasTrailingDotOrSpaceSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string s = StripExtendedPrefix(path);
        if (s.IndexOf('/') >= 0) s = s.Replace('/', '\\');
        ReadOnlySpan<char> span = s.AsSpan();
        int i = 0;
        if (span.Length >= 2 && char.IsAsciiLetter(span[0]) && span[1] == ':')
        {
            i = 2;
        }
        else if (span.StartsWith(@"\\"))
        {
            i = 2;
            for (int rootSeg = 0; rootSeg < 2 && i < span.Length; rootSeg++)
            {
                while (i < span.Length && span[i] != '\\') i++;
                while (i < span.Length && span[i] == '\\') i++;
            }
        }
        while (i < span.Length)
        {
            while (i < span.Length && span[i] == '\\') i++;
            int start = i;
            while (i < span.Length && span[i] != '\\') i++;
            if (i > start && span[i - 1] is '.' or ' ') return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether a filesystem path still exists using an exact extended-length path so trailing dots and spaces
    /// are never normalized away, and treats access/sharing errors as still existing.
    /// </summary>
    public static bool ExistsExact(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!TryToExtendedPath(path, out string extPath)) return Path.Exists(path);
        if (GetFileAttributesW(extPath) != InvalidFileAttributes) return true;
        int err = Marshal.GetLastWin32Error();
        return err is not (ErrorFileNotFound or ErrorPathNotFound);
    }

    private static bool HasAnyChildOutside(string extPath, string normalPath, HashSet<string> pendingElevation)
    {
        string pattern = extPath.EndsWith('\\') ? extPath + "*" : extPath + @"\*";
        IntPtr handle = FindFirstFileW(pattern, out WIN32_FIND_DATAW data);
        if (handle == InvalidHandleValue)
        {
            int err = Marshal.GetLastWin32Error();
            return err is not (ErrorFileNotFound or ErrorNoMoreFiles);
        }
        try
        {
            string trimmedParent = normalPath.TrimEnd('\\');
            do
            {
                if (data.cFileName is "." or "..") continue;
                if (pendingElevation.Count == 0) return true;
                string childPath = trimmedParent + "\\" + data.cFileName;
                if (!pendingElevation.Contains(childPath)) return true;
            }
            while (FindNextFileW(handle, out data));
            return Marshal.GetLastWin32Error() != ErrorNoMoreFiles;
        }
        finally
        {
            FindClose(handle);
        }
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    private static string DisplayName(string path)
    {
        string trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);
}
