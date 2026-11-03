using System.Runtime.InteropServices;
using System.Text;

namespace NaxExplorer.Shell;

/// <summary>
/// File operations and shell commands. Copy, move, delete and rename go through the Windows copy engine
/// (IFileOperation), so they show the Windows progress and conflict dialogs, ask for administrator rights only when a
/// file needs them, can be undone with Ctrl+Z in Explorer, and delete to the Recycle Bin.
/// </summary>
internal static class ShellActions
{
    /// <summary>Opens a file with its default program.</summary>
    public static bool Open(string path, IntPtr owner, string? verb = null)
    {
        var info = new Native.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_ASYNCOK,
            hwnd = owner,
            lpVerb = verb,
            lpFile = path,
            lpDirectory = Path.GetDirectoryName(path),
            nShow = 1,
        };
        return Native.ShellExecuteEx(ref info);
    }

    public static void OpenWith(string path, IntPtr owner) => Open(path, owner, "openas");

    /// <summary>Shows the Windows properties dialog of one or more items.</summary>
    public static void ShowProperties(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            var info = new Native.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
                fMask = Native.SEE_MASK_INVOKEIDLIST | Native.SEE_MASK_ASYNCOK,
                hwnd = owner,
                lpVerb = "properties",
                lpFile = paths[0],
                nShow = 1,
            };
            Native.ShellExecuteEx(ref info);
            return;
        }
        using var items = ShellItems.Create(paths);
        if (items.Array is null) return;
        Guid handler = Native.BHID_DataObject, riid = Native.IID_IDataObject;
        if (items.Array.BindToHandler(IntPtr.Zero, ref handler, ref riid, out IntPtr data) != 0 || data == IntPtr.Zero) return;
        object dataObject = Marshal.GetObjectForIUnknown(data);
        Marshal.Release(data);
        Native.SHMultiFileProperties(dataObject, 0);
        Native.Release(dataObject);
    }

    /// <summary>The folder a shortcut points to, or null when it points to a file or cannot be read.</summary>
    public static string? ShortcutFolder(string path)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_ShellLink)!);
            if (link is not IPersistFile persist || persist.Load(path, 0) != 0) return null;
            var target = new StringBuilder(1024);
            if (((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0) != 0) return null;
            string result = target.ToString();
            return result.Length > 0 && Directory.Exists(result) ? result : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Native.Release(link);
        }
    }

    public static Task CopyAsync(IReadOnlyList<string> sources, string folder, IntPtr owner) => TransferAsync(sources, folder, owner, move: false);

    public static Task MoveAsync(IReadOnlyList<string> sources, string folder, IntPtr owner) => TransferAsync(sources, folder, owner, move: true);

    private static Task TransferAsync(IReadOnlyList<string> sources, string folder, IntPtr owner, bool move)
    {
        // Copies into the folder the items already live in get "- Copy" names, as in Explorer; others ask on conflicts.
        var sameFolder = sources.Where(s => Core.PathTools.SameLocation(Path.GetDirectoryName(s.TrimEnd('\\')) ?? "", folder)).ToList();
        var elsewhere = sources.Except(sameFolder).ToList();
        return RunOnSta(() =>
        {
            if (elsewhere.Count > 0) Perform(owner, 0, op => Transfer(op, elsewhere, folder, move));
            if (sameFolder.Count > 0 && !move) Perform(owner, Native.FOF_RENAMEONCOLLISION, op => Transfer(op, sameFolder, folder, move: false));
        });
    }

    private static int Transfer(IFileOperation operation, IReadOnlyList<string> sources, string folder, bool move)
    {
        using var items = ShellItems.Create(sources);
        if (items.Array is null) return -1;
        Guid riid = Native.IID_IShellItem;
        if (Native.SHCreateItemFromParsingName(folder, IntPtr.Zero, ref riid, out IShellItem destination) != 0) return -1;
        try
        {
            return move ? operation.MoveItems(items.Array, destination) : operation.CopyItems(items.Array, destination);
        }
        finally
        {
            Native.Release(destination);
        }
    }

    /// <summary>Deletes to the Recycle Bin, or permanently with <paramref name="permanently"/>, which Windows confirms first.</summary>
    public static Task DeleteAsync(IReadOnlyList<string> paths, IntPtr owner, bool permanently) => RunOnSta(() =>
    {
        uint flags = permanently ? 0 : Native.FOF_ALLOWUNDO | Native.FOFX_RECYCLEONDELETE;
        Perform(owner, flags, operation =>
        {
            using var items = ShellItems.Create(paths);
            return items.Array is null ? -1 : operation.DeleteItems(items.Array);
        });
    });

    public static Task RenameAsync(string path, string newName, IntPtr owner) => RunOnSta(() =>
    {
        Perform(owner, 0, operation =>
        {
            Guid riid = Native.IID_IShellItem;
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out IShellItem item) != 0) return -1;
            try
            {
                return operation.RenameItem(item, newName, IntPtr.Zero);
            }
            finally
            {
                Native.Release(item);
            }
        });
    });

    private static void Perform(IntPtr owner, uint extraFlags, Func<IFileOperation, int> queue)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_FileOperation)!)!;
        try
        {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | Native.FOFX_ADDUNDORECORD | Native.FOFX_SHOWELEVATIONPROMPT | extraFlags);
            if (queue(operation) != 0) return;
            operation.PerformOperations();
        }
        finally
        {
            Native.Release(operation);
        }
    }

    /// <summary>Runs shell work on its own STA thread, so a long copy never blocks the window.</summary>
    private static Task RunOnSta(Action work)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "File operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>A shell item array built from paths, with the PIDLs it needs freed afterwards.</summary>
internal sealed class ShellItems : IDisposable
{
    private readonly List<IntPtr> _pidls;

    public IShellItemArray? Array { get; }

    public IReadOnlyList<IntPtr> Pidls => _pidls;

    private ShellItems(List<IntPtr> pidls, IShellItemArray? array)
    {
        _pidls = pidls;
        Array = array;
    }

    public static ShellItems Create(IEnumerable<string> paths)
    {
        var pidls = new List<IntPtr>();
        foreach (string path in paths)
        {
            if (Native.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero) pidls.Add(pidl);
        }
        IShellItemArray? array = null;
        if (pidls.Count > 0 && Native.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out IShellItemArray created) == 0) array = created;
        return new ShellItems(pidls, array);
    }

    public void Dispose()
    {
        Native.Release(Array);
        foreach (IntPtr pidl in _pidls) Native.ILFree(pidl);
        _pidls.Clear();
    }
}

/// <summary>
/// The Windows context menu of files and folders ("Show more options"), with every shell extension such as 7-Zip or
/// "Open with". Submenus like "Send to" are filled only when the window forwards menu messages, which the main window
/// does through <see cref="TryHandleMessage"/>.
/// </summary>
internal static class ShellContextMenu
{
    private static object? s_active;

    /// <summary>Shows the menu at a screen point. "rename" is handed back to the app, which renames in place.</summary>
    public static void Show(IntPtr owner, IReadOnlyList<string> paths, Native.POINT screen, Action renameInPlace)
    {
        if (paths.Count == 0) return;
        // Every item must share the folder of the first one; others are left out, as the shell menu needs one folder.
        string parent = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? "";
        var sameFolder = paths.Where(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')) ?? "", parent, StringComparison.OrdinalIgnoreCase)).ToList();
        using var items = ShellItems.Create(sameFolder);
        if (items.Pidls.Count == 0) return;
        Guid folderId = Native.IID_IShellFolder;
        if (Native.SHBindToParent(items.Pidls[0], ref folderId, out IntPtr folderPointer, out _) != 0) return;
        var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
        Marshal.Release(folderPointer);
        IntPtr menu = Native.CreatePopupMenu();
        object? contextMenu = null;
        try
        {
            IntPtr[] children = items.Pidls.Select(Native.ILFindLastID).ToArray();
            Guid menuId = Native.IID_IContextMenu;
            if (folder.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out IntPtr menuPointer) != 0) return;
            contextMenu = Marshal.GetObjectForIUnknown(menuPointer);
            Marshal.Release(menuPointer);
            var context = (IContextMenu)contextMenu;
            uint flags = Native.CMF_NORMAL | Native.CMF_EXPLORE | Native.CMF_CANRENAME;
            if (Native.IsKeyDown(0x10)) flags |= Native.CMF_EXTENDEDVERBS;
            if (context.QueryContextMenu(menu, 0, 1, 0x7FFF, flags) < 0) return;
            s_active = contextMenu;
            Native.SetForegroundWindow(owner);
            uint command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, screen.X, screen.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            s_active = null;
            if (command == 0) return;
            if (Verb(context, command - 1) is "rename")
            {
                renameInPlace();
                return;
            }
            var invoke = new Native.CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE,
                hwnd = owner,
                lpVerb = (IntPtr)(command - 1),
                lpVerbW = (IntPtr)(command - 1),
                lpDirectoryW = IntPtr.Zero,
                nShow = 1,
                ptInvoke = screen,
            };
            context.InvokeCommand(ref invoke);
        }
        catch (COMException)
        {
        }
        finally
        {
            s_active = null;
            Native.DestroyMenu(menu);
            Native.Release(contextMenu);
            Native.Release(folder);
        }
    }

    private static string? Verb(IContextMenu menu, uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (menu.GetCommandString((UIntPtr)offset, Native.GCS_VERBW, IntPtr.Zero, buffer, 256) != 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool TryHandleMessage(int message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        object? active = s_active;
        if (active is null || message is not (Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM or Native.WM_MENUCHAR)) return false;
        try
        {
            if (active is IContextMenu3 menu3) return menu3.HandleMenuMsg2((uint)message, wParam, lParam, out result) == 0;
            if (active is IContextMenu2 menu2) return menu2.HandleMenuMsg((uint)message, wParam, lParam) == 0;
        }
        catch (COMException)
        {
        }
        return false;
    }
}

/// <summary>Files on the Windows clipboard in the format Explorer uses, so copy and paste work between both apps.</summary>
internal static class FileClipboard
{
    private static readonly uint DropEffectFormat = Native.RegisterClipboardFormat("Preferred DropEffect");

    public static bool Put(IReadOnlyList<string> paths, bool cut, IntPtr owner)
    {
        if (paths.Count == 0 || !Open(owner)) return false;
        try
        {
            Native.EmptyClipboard();
            // DROPFILES followed by the paths, each ending with a NUL, and one more NUL at the end.
            string list = string.Join('\0', paths) + "\0\0";
            int header = Marshal.SizeOf<Native.DROPFILES>();
            int bytes = header + list.Length * 2;
            IntPtr drop = Native.GlobalAlloc(Native.GMEM_MOVEABLE | Native.GMEM_ZEROINIT, (UIntPtr)bytes);
            IntPtr target = Native.GlobalLock(drop);
            Marshal.StructureToPtr(new Native.DROPFILES { pFiles = header, fWide = 1 }, target, false);
            Marshal.Copy(list.ToCharArray(), 0, target + header, list.Length);
            Native.GlobalUnlock(drop);
            if (Native.SetClipboardData(Native.CF_HDROP, drop) == IntPtr.Zero)
            {
                Native.GlobalFree(drop);
                return false;
            }
            IntPtr effect = Native.GlobalAlloc(Native.GMEM_MOVEABLE, 4);
            Marshal.WriteInt32(Native.GlobalLock(effect), (int)(cut ? Native.DROPEFFECT_MOVE : Native.DROPEFFECT_COPY));
            Native.GlobalUnlock(effect);
            if (Native.SetClipboardData(DropEffectFormat, effect) == IntPtr.Zero) Native.GlobalFree(effect);
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>The files on the clipboard and whether they were cut, or null when it holds no files.</summary>
    public static (List<string> Paths, bool Cut)? Get(IntPtr owner)
    {
        if (!Native.IsClipboardFormatAvailable(Native.CF_HDROP) || !Open(owner)) return null;
        try
        {
            IntPtr drop = Native.GetClipboardData(Native.CF_HDROP);
            if (drop == IntPtr.Zero) return null;
            uint count = Native.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint length = Native.DragQueryFile(drop, i, null, 0);
                var builder = new StringBuilder((int)length + 1);
                Native.DragQueryFile(drop, i, builder, length + 1);
                paths.Add(builder.ToString());
            }
            bool cut = false;
            IntPtr effect = Native.GetClipboardData(DropEffectFormat);
            if (effect != IntPtr.Zero)
            {
                IntPtr value = Native.GlobalLock(effect);
                if (value != IntPtr.Zero)
                {
                    cut = (Marshal.ReadInt32(value) & Native.DROPEFFECT_MOVE) != 0;
                    Native.GlobalUnlock(effect);
                }
            }
            return (paths, cut);
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static bool HasFiles => Native.IsClipboardFormatAvailable(Native.CF_HDROP);

    /// <summary>Empties the clipboard after cut files were pasted, as Explorer does.</summary>
    public static void Clear(IntPtr owner)
    {
        if (!Open(owner)) return;
        Native.EmptyClipboard();
        Native.CloseClipboard();
    }

    private static bool Open(IntPtr owner)
    {
        // Another program may hold the clipboard for a moment.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }
}
