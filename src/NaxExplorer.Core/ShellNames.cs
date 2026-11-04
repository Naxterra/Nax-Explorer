using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NaxExplorer.Core;

/// <summary>
/// The names Windows shows for folders and drives. Folders such as C:\Users or Pictures carry a localized name in their
/// desktop.ini ("Benutzer", "Bilder" on a German Windows), and drives show their label and letter ("Lokaler
/// Datenträger (C:)"). The names are asked from Windows once per path and kept.
/// </summary>
public static partial class ShellNames
{
    private const uint ShgfiDisplayName = 0x200;

    /// <summary>Big searches pass many folders the user never sees again; the store starts over when it holds this many names.</summary>
    internal const int MaxStored = 20000;

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for folders that may carry a localized name. Windows reads a folder's desktop.ini only when the folder has
    /// the read-only or system attribute, so all other folders keep their plain name without asking Windows.
    /// </summary>
    public static bool MayBeLocalized(FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) != 0 && (attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0;

    /// <summary>
    /// The name Windows shows for a folder or drive root, or the last part of the path when Windows gives none. A drive
    /// root is asked every time, because its letter can get another medium and a drive can be renamed; the stored
    /// name then follows the current one.
    /// </summary>
    public static string Of(string path)
    {
        if (!IsRoot(path) && Cache.TryGetValue(path, out string? cached)) return cached;
        string name = Ask(path) ?? Fallback(path);
        Store(path, name);
        return name;
    }

    /// <summary>
    /// Like <see cref="Of"/> for a folder whose attributes are not known yet: Windows is asked only when the attributes
    /// allow a localized name, so plain folders cost no call into the shell.
    /// </summary>
    public static string OfFolder(string path)
    {
        if (IsRoot(path)) return Of(path);
        if (Cache.TryGetValue(path, out string? cached)) return cached;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreachable folder keeps its plain name and is asked again next time.
            return Fallback(path);
        }
        if (MayBeLocalized(attributes)) return Of(path);
        string name = Fallback(path);
        Store(path, name);
        return name;
    }

    /// <summary>
    /// True for the root of a drive in this PC: a fixed, removable or optical drive, a mounted disc image or a RAM disk.
    /// Its name comes at once while its folders are open, and it may hold another stick or disc than before. Network
    /// drives and shares can take seconds to answer.
    /// </summary>
    public static bool IsLocalDrive(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Drive roots whose name is being asked right now, so a slow network drive is never asked twice at once.</summary>
    private static readonly ConcurrentDictionary<string, byte> Asking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name of a drive root for the address bar, called off the UI thread. A drive in this PC is asked every time
    /// (see <see cref="IsLocalDrive"/>). A network drive or share is asked only while no name is stored for it, and
    /// only by one thread at a time; afterwards it keeps its stored name, which This PC refreshes when it lists it.
    /// </summary>
    public static string OfDrive(string root)
    {
        if (IsLocalDrive(root)) return Of(root);
        if (Cache.TryGetValue(root, out string? stored)) return stored;
        if (!Asking.TryAdd(root, 0)) return Fallback(root);
        try
        {
            return Of(root);
        }
        finally
        {
            Asking.TryRemove(root, out _);
        }
    }

    /// <summary>The name for the address bar and tabs, without asking Windows: the stored name, or the plain one.</summary>
    public static string Known(string path) => Cache.TryGetValue(path, out string? cached) ? cached : Fallback(path);

    /// <summary>A folder path written with the names Windows shows, such as C:\Benutzer\Anna, asking Windows for names not stored yet.</summary>
    public static string PathOf(string location) => PathTools.DisplayPath(location, OfFolder);

    /// <summary>The same path from the stored names only, without asking Windows, for code on the UI thread.</summary>
    public static string KnownPath(string location) => PathTools.DisplayPath(location, Known);

    /// <summary>The number of stored names, for tests.</summary>
    internal static int StoredCount => Cache.Count;

    internal static void Store(string path, string name)
    {
        if (Cache.Count >= MaxStored) Cache.Clear();
        Cache[path] = name;
    }

    private static bool IsRoot(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && string.Equals(root.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Fallback(string path)
    {
        string trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed.TrimEnd('\\') : name;
    }

    private static string? Ask(string path)
    {
        var info = new FileInfoW();
        try
        {
            if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<FileInfoW>(), ShgfiDisplayName) == IntPtr.Zero) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        string name = info.DisplayName ?? "";
        return name.Length > 0 ? name : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoW
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref FileInfoW info, uint size, uint flags);
}
