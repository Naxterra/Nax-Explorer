using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NaxExplorer.Core;

namespace NaxExplorer.Shell;

[ComImport, Guid("70629033-E363-4A28-A567-0DB78006E6D7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
}

[ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    void GetPropertyStore();
    void GetPropertyStoreWithCreateObject();
    void GetPropertyStoreForKeys();
    void GetPropertyDescriptionList();
    void Update();
    void GetProperty();
    void GetCLSID();
    [PreserveSig] int GetFileTime(ref PropertyKey key, out FILETIME time);
    void GetInt32();
    [PreserveSig] int GetString(ref PropertyKey key, out IntPtr text);
    void GetUInt32();
    [PreserveSig] int GetUInt64(ref PropertyKey key, out ulong value);
    void GetBool();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid format, uint id)
{
    public Guid Format = format;
    public uint Id = id;
}

/// <summary>What a shell place shows: its own name, the place one level up, and its items.</summary>
internal sealed record ShellListing(string Name, string Parent, List<FileEntry> Entries);

/// <summary>
/// Lists places that are not folders on disk the way Windows lists them, such as the Recycle Bin, Network, a computer
/// under Network, a library, a phone or a folder inside a zip file. Items that are ordinary files and folders, such as
/// the shares of a computer or the folders of a library, keep their real paths, so they open in the app as usual;
/// everything else keeps its shell identity (see <see cref="PathTools.ShellPrefix"/>). The work runs on its own STA
/// thread, because some shell folders only answer there, and it never opens a file's contents.
/// </summary>
internal static class ShellFolderReader
{
    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;
    private const uint SigdnFileSysPath = 0x80058000;
    private const uint SfgaoFolder = 0x20000000;
    private const uint SfgaoFileSystem = 0x40000000;
    private const uint SfgaoStream = 0x00400000;
    private const uint SfgaoHidden = 0x00080000;

    private static readonly Guid BhidEnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    private static readonly Guid IidEnumShellItems = typeof(IEnumShellItems).GUID;
    private static readonly Guid FmtStorage = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    private static readonly Guid FmtRecycle = new("9B174B33-40FF-11D2-A27E-00C04FC30871");

    /// <summary>The Recycle Bin's parsing name; its items keep their shell identity, so they can be restored.</summary>
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>The names Windows shows for shell places the app has listed, for tabs and the address bar.</summary>
    private static readonly ConcurrentDictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name of a shell place, as far as it is known yet; otherwise null.</summary>
    public static string? KnownName(string location) => s_names.TryGetValue(location, out string? name) ? name : null;

    /// <summary>Lists a shell place. Throws an IOException with Windows' message when the place cannot be listed.</summary>
    public static Task<ShellListing> ReadAsync(string location, bool showHidden, CancellationToken token) =>
        RunOnSta(() =>
        {
            try
            {
                return Read(location, showHidden, token);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A shell folder that fails in an unexpected way is reported like a folder that cannot be read.
                throw new IOException(ex.Message, ex);
            }
        });

    /// <summary>
    /// What the app should open for something typed or handed to it: a folder path stays as it is, and a place only
    /// Windows can list becomes a shell place. Returns null for anything that is not a folder of either kind.
    /// </summary>
    public static Task<string?> ResolveAsync(string text) => RunOnSta(() =>
    {
        Guid riid = Native.IID_IShellItem;
        if (Native.SHCreateItemFromParsingName(PathTools.ParsingName(text), IntPtr.Zero, ref riid, out IShellItem item) != 0) return null;
        try
        {
            if (item.GetAttributes(SfgaoFolder | SfgaoFileSystem | SfgaoStream, out uint attributes) < 0) return null;
            // ZIP files expose both folder enumeration and a byte stream.
            if ((attributes & SfgaoFolder) == 0) return null;
            string? path = (attributes & SfgaoFileSystem) != 0 ? Name(item, SigdnFileSysPath) : null;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            string? parsing = Name(item, SigdnDesktopAbsoluteParsing);
            if (string.IsNullOrEmpty(parsing)) return null;
            string place = PathTools.ShellPlace(parsing);
            if (Name(item, SigdnNormalDisplay) is { Length: > 0 } shown) s_names[place] = shown;
            return place;
        }
        finally
        {
            Native.Release(item);
        }
    });

    private static ShellListing Read(string location, bool showHidden, CancellationToken token)
    {
        Guid riid = Native.IID_IShellItem;
        int created = Native.SHCreateItemFromParsingName(PathTools.ParsingName(location), IntPtr.Zero, ref riid, out IShellItem folder);
        if (created != 0) throw new IOException(Marshal.GetExceptionForHR(created)?.Message ?? Loc.T("FolderNotFound"));
        try
        {
            string name = Name(folder, SigdnNormalDisplay) ?? location;
            s_names[location] = name;
            string parent = ParentOf(folder);
            string? parsing = Name(folder, SigdnDesktopAbsoluteParsing);
            bool keepShellIdentity = parsing is not null && parsing.StartsWith(RecycleBin, StringComparison.OrdinalIgnoreCase);
            Guid handler = BhidEnumItems, enumId = IidEnumShellItems;
            int bound = folder.BindToHandler(IntPtr.Zero, ref handler, ref enumId, out IntPtr enumPointer);
            if (bound != 0 || enumPointer == IntPtr.Zero) throw new IOException(Marshal.GetExceptionForHR(bound)?.Message ?? Loc.T("AccessDenied"));
            var entries = new List<FileEntry>();
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPointer);
            Marshal.Release(enumPointer);
            try
            {
                while (!token.IsCancellationRequested && items.Next(1, out IShellItem child, out uint fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        if (Entry(child, keepShellIdentity, showHidden) is { } entry) entries.Add(entry);
                    }
                    finally
                    {
                        Native.Release(child);
                    }
                }
            }
            finally
            {
                Native.Release(items);
            }
            token.ThrowIfCancellationRequested();
            return new ShellListing(name, parent, entries);
        }
        finally
        {
            Native.Release(folder);
        }
    }

    private static FileEntry? Entry(IShellItem child, bool keepShellIdentity, bool showHidden)
    {
        if (child.GetAttributes(SfgaoFolder | SfgaoFileSystem | SfgaoStream | SfgaoHidden, out uint attributes) < 0) return null;
        if (!showHidden && (attributes & SfgaoHidden) != 0) return null;
        string? shown = Name(child, SigdnNormalDisplay);
        string? parsing = Name(child, SigdnDesktopAbsoluteParsing);
        if (string.IsNullOrEmpty(shown) || string.IsNullOrEmpty(parsing)) return null;
        // The Recycle Bin names its items by their original full path; the list shows the name, as Explorer does.
        if (keepShellIdentity && shown.Contains('\\') && Path.GetFileName(shown.TrimEnd('\\')) is { Length: > 0 } name) shown = name;
        // Stream-backed archives stay files here; the pane opens ZIPs through its compressed-folder route.
        bool folder = (attributes & SfgaoFolder) != 0 && (attributes & SfgaoStream) == 0;
        string? path = !keepShellIdentity && (attributes & SfgaoFileSystem) != 0 ? Name(child, SigdnFileSysPath) : null;
        string full = !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && Path.Exists(path) ? path : PathTools.ShellPlace(parsing);
        if (PathTools.IsShellPlace(full)) s_names[full] = shown;
        long size = 0;
        DateTime modified = default;
        string? type = null;
        if (child is IShellItem2 rich)
        {
            var sizeKey = new PropertyKey(FmtStorage, 12);
            if (!folder && rich.GetUInt64(ref sizeKey, out ulong bytes) == 0) size = (long)Math.Min(bytes, long.MaxValue);
            var dateKey = keepShellIdentity ? new PropertyKey(FmtRecycle, 3) : new PropertyKey(FmtStorage, 14);
            if (rich.GetFileTime(ref dateKey, out FILETIME time) == 0) modified = ToUtc(time);
            var typeKey = new PropertyKey(FmtStorage, 4);
            if (rich.GetString(ref typeKey, out IntPtr text) == 0 && text != IntPtr.Zero)
            {
                type = Marshal.PtrToStringUni(text);
                Marshal.FreeCoTaskMem(text);
            }
        }
        return new FileEntry
        {
            Name = shown,
            FullPath = full,
            Kind = folder ? EntryKind.Folder : EntryKind.File,
            Size = size,
            ModifiedUtc = modified,
            Attributes = (attributes & SfgaoHidden) != 0 ? FileAttributes.Hidden : FileAttributes.Normal,
            TypeName = type,
        };
    }

    /// <summary>The place one level up: a folder path, another shell place, or This PC at the top.</summary>
    private static string ParentOf(IShellItem folder)
    {
        if (folder.GetParent(out IShellItem parent) != 0) return PathTools.ThisPc;
        try
        {
            // The desktop is the top of the shell; the app's top is This PC.
            if (parent.GetParent(out IShellItem grand) != 0) return PathTools.ThisPc;
            Native.Release(grand);
            parent.GetAttributes(SfgaoFileSystem, out uint attributes);
            if ((attributes & SfgaoFileSystem) != 0 && Name(parent, SigdnFileSysPath) is { Length: > 0 } path && Directory.Exists(path)) return path;
            string? parsing = Name(parent, SigdnDesktopAbsoluteParsing);
            if (string.IsNullOrEmpty(parsing)) return PathTools.ThisPc;
            // This PC itself is the app's own list of drives.
            if (parsing.Equals("::" + ShellPlaces.ThisPcClsid, StringComparison.OrdinalIgnoreCase)) return PathTools.ThisPc;
            string place = PathTools.ShellPlace(parsing);
            if (Name(parent, SigdnNormalDisplay) is { Length: > 0 } shown) s_names[place] = shown;
            return place;
        }
        finally
        {
            Native.Release(parent);
        }
    }

    private static string? Name(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static DateTime ToUtc(FILETIME time)
    {
        long ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        return ticks <= 0 ? default : DateTime.FromFileTimeUtc(ticks);
    }

    internal static Task<T> RunOnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Shell place" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
