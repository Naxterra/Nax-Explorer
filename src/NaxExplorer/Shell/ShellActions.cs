using System.Runtime.InteropServices;
using System.Text;

namespace NaxExplorer.Shell;

/// <summary>
/// File operations and shell commands. Copy, move, delete and rename go through the Windows copy engine
/// (IFileOperation), with the app's own progress and conflict UI. Operations ask for administrator rights only when a
/// file needs them, can be undone with Ctrl+Z in Explorer, and normally delete to the Recycle Bin.
/// </summary>
internal static class ShellActions
{
    /// <summary>Opens a file with its default program, or an item of a shell place with its default command.</summary>
    public static bool Open(string path, IntPtr owner, string? verb = null)
    {
        if (Core.PathTools.IsShellPlace(path)) return InvokeShellItem(path, owner, verb);
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
        if (paths.Count == 1 && Core.PathTools.IsShellPlace(paths[0]))
        {
            InvokeShellItem(paths[0], owner, "properties");
            return;
        }
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

    /// <summary>
    /// Runs a command on an item that has no path of its own, such as a deleted file in the Recycle Bin or a file on a
    /// phone, through the item's identity in the shell. Without <paramref name="verb"/> its default command runs.
    /// </summary>
    private static bool InvokeShellItem(string location, IntPtr owner, string? verb)
    {
        if (Native.SHParseDisplayName(location, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
        try
        {
            var info = new Native.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
                fMask = Native.SEE_MASK_INVOKEIDLIST,
                hwnd = owner,
                lpVerb = verb,
                lpIDList = pidl,
                nShow = 1,
            };
            return Native.ShellExecuteEx(ref info);
        }
        finally
        {
            Native.ILFree(pidl);
        }
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

    /// <summary>
    /// Runs the planned batches of a copy or move one after another (see <see cref="Core.TransferPlan"/>). Every batch
    /// is attempted even when an earlier one failed, and all failures are reported together.
    /// </summary>
    public static Task TransferAsync(IReadOnlyList<Core.TransferBatch> batches, bool move, IntPtr owner, OperationProgress progress) => RunOnSta(() =>
    {
        var errors = new List<COMException>();
        for (int i = 0; i < batches.Count && !progress.IsCancelled; i++)
        {
            Core.TransferBatch batch = batches[i];
            // A copy into the folder the items live in is always a copy, also as part of a move request.
            bool batchMove = move && !batch.Sources.All(s => Core.PathTools.SameLocation(Path.GetDirectoryName(s.TrimEnd('\\')) ?? "", batch.TargetFolder));
            progress.BeginBatch(i, batches.Count);
            Attempt(errors, () => Perform(owner, batch.Flags, progress, op => Transfer(op, batch.Sources, batch.TargetFolder, batchMove)));
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new COMException(string.Join(" ", errors.Select(e => e.Message).Distinct()), errors[0].ErrorCode);
    });

    private static void Attempt(List<COMException> errors, Action work)
    {
        try
        {
            work();
        }
        catch (COMException ex)
        {
            errors.Add(ex);
        }
    }

    private static int Transfer(IFileOperation operation, IReadOnlyList<string> sources, string folder, bool move)
    {
        using var items = ShellItems.Create(sources, requireAll: true);
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

    /// <summary>
    /// Deletes to the Recycle Bin, or permanently with <paramref name="permanently"/>, after the user confirmed it in the
    /// app. An item that cannot go to the Recycle Bin still brings up Windows' warning first.
    /// </summary>
    public static Task DeleteAsync(IReadOnlyList<string> paths, IntPtr owner, bool permanently, OperationProgress progress) => RunOnSta(() =>
    {
        Perform(owner, Core.OperationFlags.ForDelete(permanently), progress, operation =>
        {
            using var items = ShellItems.Create(paths, requireAll: true);
            return items.Array is null ? -1 : operation.DeleteItems(items.Array);
        });
    });

    /// <summary>Renames one item. The caller checks first that the new name is free.</summary>
    public static Task RenameAsync(string path, string newName, IntPtr owner) => RunOnSta(() =>
    {
        Perform(owner, Core.OperationFlags.ForRename(), null, operation =>
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

    /// <summary>
    /// Runs one operation with exactly the given flags (see <see cref="Core.OperationFlags"/>). With
    /// <paramref name="progress"/>, the app follows it for its own progress window and can cancel it. Items that failed
    /// are collected there; a failure of the whole operation is thrown.
    /// </summary>
    private static void Perform(IntPtr owner, uint flags, OperationProgress? progress, Func<IFileOperation, int> queue)
    {
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_FileOperation)!)!;
        uint cookie = 0;
        bool advised = false;
        // The progress collects the failures of every batch of a request; only those of this run excuse its result.
        int failuresBefore = progress?.Failures.Count ?? 0;
        try
        {
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            if (progress is not null)
            {
                Marshal.ThrowExceptionForHR(operation.Advise(progress, out cookie));
                advised = true;
            }
            // A failure is reported to the caller, which shows it, instead of the operation silently doing nothing.
            int queued = queue(operation);
            if (queued != 0) throw new COMException(Loc.T("OperationNotStarted"), queued == -1 ? unchecked((int)0x80004005) : queued);
            int done = operation.PerformOperations();
            if (progress is not null && (IsCancel(done) || operation.GetAnyOperationsAborted(out bool aborted) == 0 && aborted)) progress.Cancel();
            // Only a failure counts: the copy engine also returns success codes other than S_OK, for example when an
            // item was skipped. Failed items are already listed by the progress; anything else is thrown.
            bool listed = progress is not null && progress.Failures.Count > failuresBefore;
            if (done < 0 && !IsCancel(done) && !listed) throw new COMException(Marshal.GetExceptionForHR(done)?.Message, done);
        }
        finally
        {
            if (advised) operation.Unadvise(cookie);
            Native.Release(operation);
        }
    }

    /// <summary>True for the results Windows gives when the user cancelled in its own dialog, which is no error.</summary>
    private static bool IsCancel(int hr) =>
        hr == unchecked((int)0x800704C7) // ERROR_CANCELLED
        || hr == unchecked((int)0x80270000); // COPYENGINE_E_USER_CANCELLED

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

    public static ShellItems Create(IEnumerable<string> paths, bool requireAll = false)
    {
        var pidls = new List<IntPtr>();
        try
        {
            foreach (string path in paths)
            {
                int parsed = Native.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _);
                if (parsed == 0 && pidl != IntPtr.Zero) pidls.Add(pidl);
                else if (pidl != IntPtr.Zero) Native.ILFree(pidl);
                if (requireAll && (parsed != 0 || pidl == IntPtr.Zero))
                    throw new COMException(Loc.T("OperationNotStarted"), parsed < 0 ? parsed : unchecked((int)0x80004005));
            }
            IShellItemArray? array = null;
            if (pidls.Count > 0)
            {
                int created = Native.SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out IShellItemArray items);
                if (created == 0) array = items;
                else if (requireAll) throw new COMException(Loc.T("OperationNotStarted"), created);
            }
            return new ShellItems(pidls, array);
        }
        catch
        {
            foreach (IntPtr pidl in pidls) Native.ILFree(pidl);
            throw;
        }
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
    public static (List<string> Paths, bool Cut, Core.CutPasteCompletion Completion)? Get(IntPtr owner)
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
            return (paths, cut, new Core.CutPasteCompletion(paths, Native.GetClipboardSequenceNumber()));
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static bool HasFiles => Native.IsClipboardFormatAvailable(Native.CF_HDROP);

    /// <summary>Empties the clipboard after cut files were pasted, as Explorer does.</summary>
    public static bool ClearIfUnchanged(IntPtr owner, Core.CutPasteCompletion completion)
    {
        if (!Open(owner)) return false;
        try
        {
            return completion.OwnsClipboard(Native.GetClipboardSequenceNumber()) && Native.EmptyClipboard();
        }
        finally { Native.CloseClipboard(); }
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
