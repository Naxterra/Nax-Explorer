using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NaxExplorer.Shell;

/// <summary>One entry of a Windows context menu, read from its Win32 menu so the app can show it in its own menu.</summary>
internal sealed record ShellMenuEntry(uint Id, string Text, string? Verb, bool Enabled, bool Separator, ImagePixels? Icon, IReadOnlyList<ShellMenuEntry> Children);

/// <summary>
/// The Windows context menu of some items, or of a folder's background, built on its own STA thread. Shell extensions
/// load there, so the window never waits for a slow one, and a chosen command runs there too. The thread keeps pumping
/// messages for a minute after a command, because some commands (such as "Send to compressed folder") finish later.
/// </summary>
internal sealed class ShellMenuSession
{
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;

    private readonly BlockingCollection<Action> _work = new();
    private object? _menu;
    private IntPtr _menuHandle;
    private IShellFolder? _folder;
    private ShellItems? _items;
    private volatile bool _stopping;
    private long _stopAtTicks = long.MaxValue;

    private ShellMenuSession()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "Shell menu" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Menus still being built, with the time each was asked for. A build stuck in a shell extension stays here.</summary>
    private static readonly ConcurrentDictionary<ShellMenuSession, long> Building = new();

    /// <summary>A build that takes longer than this is taken as stuck in a shell extension.</summary>
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(3);

    /// <summary>The menu of items that share one folder, as Explorer shows it for them, or null while an earlier build is stuck.</summary>
    public static Task<(ShellMenuSession Session, List<ShellMenuEntry> Entries)?> ForItemsAsync(IReadOnlyList<string> paths, IntPtr owner, bool extended)
    {
        if (OneIsStuck()) return Task.FromResult<(ShellMenuSession, List<ShellMenuEntry>)?>(null);
        var session = new ShellMenuSession();
        return session.Build(() => session.BuildForItems(paths, owner, extended));
    }

    /// <summary>The menu of a folder's empty area, which holds Windows' "New" submenu with every installed file template.</summary>
    public static Task<(ShellMenuSession Session, List<ShellMenuEntry> Entries)?> ForBackgroundAsync(string folder, IntPtr owner)
    {
        if (OneIsStuck()) return Task.FromResult<(ShellMenuSession, List<ShellMenuEntry>)?>(null);
        var session = new ShellMenuSession();
        return session.Build(() => session.BuildForBackground(folder, owner));
    }

    /// <summary>
    /// True while a build hangs in a shell extension. No further menu thread starts then, so a hanging extension costs
    /// one thread and not one per right-click; the app's own menu still opens, without Windows' entries.
    /// </summary>
    private static bool OneIsStuck()
    {
        long limit = DateTime.UtcNow.Ticks - StuckAfter.Ticks;
        return Building.Values.Any(started => started < limit);
    }

    private Task<(ShellMenuSession, List<ShellMenuEntry>)?> Build(Func<List<ShellMenuEntry>?> build)
    {
        var done = new TaskCompletionSource<(ShellMenuSession, List<ShellMenuEntry>)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Building[this] = DateTime.UtcNow.Ticks;
        _work.Add(() =>
        {
            List<ShellMenuEntry>? entries = null;
            try
            {
                entries = build();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A broken shell extension leaves the app's own menu without Windows' entries, nothing worse.
                if (ex is not (COMException or InvalidCastException)) CrashLog.Write(ex);
            }
            finally
            {
                Building.TryRemove(this, out _);
            }
            if (entries is null)
            {
                Close();
                done.SetResult(null);
            }
            else
            {
                done.SetResult((this, entries));
            }
        });
        return done.Task;
    }

    private void Loop()
    {
        while (true)
        {
            if (_work.TryTake(out Action? action, 30))
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    CrashLog.Write(ex);
                }
            }
            Pump();
            if (_stopping && _work.Count == 0 && DateTime.UtcNow.Ticks >= Interlocked.Read(ref _stopAtTicks)) break;
        }
        Release();
    }

    private static void Pump()
    {
        while (Native.PeekMessage(out Native.MSG message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    private List<ShellMenuEntry>? BuildForItems(IReadOnlyList<string> paths, IntPtr owner, bool extended)
    {
        if (paths.Count == 0) return null;
        // The shell builds one menu for items of one folder; items from other folders are left out, as Explorer does.
        string parent = Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? "";
        var sameFolder = paths.Where(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')) ?? "", parent, StringComparison.OrdinalIgnoreCase)).ToList();
        _items = ShellItems.Create(sameFolder);
        if (_items.Pidls.Count == 0) return null;
        Guid folderId = Native.IID_IShellFolder;
        if (Native.SHBindToParent(_items.Pidls[0], ref folderId, out IntPtr folderPointer, out _) != 0) return null;
        _folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
        Marshal.Release(folderPointer);
        IntPtr[] children = _items.Pidls.Select(Native.ILFindLastID).ToArray();
        Guid menuId = Native.IID_IContextMenu;
        if (_folder.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out IntPtr menuPointer) != 0) return null;
        _menu = Marshal.GetObjectForIUnknown(menuPointer);
        Marshal.Release(menuPointer);
        return Query(extended);
    }

    private List<ShellMenuEntry>? BuildForBackground(string folder, IntPtr owner)
    {
        if (Native.SHParseDisplayName(folder, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
        IShellFolder? desktop = null;
        try
        {
            if (Native.SHGetDesktopFolder(out desktop) != 0) return null;
            Guid folderId = Native.IID_IShellFolder;
            if (desktop.BindToObject(pidl, IntPtr.Zero, ref folderId, out IntPtr folderPointer) != 0) return null;
            _folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            Guid menuId = Native.IID_IContextMenu;
            if (_folder.CreateViewObject(owner, ref menuId, out IntPtr menuPointer) != 0) return null;
            _menu = Marshal.GetObjectForIUnknown(menuPointer);
            Marshal.Release(menuPointer);
            return Query(extended: false);
        }
        finally
        {
            Native.Release(desktop);
            Native.ILFree(pidl);
        }
    }

    private List<ShellMenuEntry> Query(bool extended)
    {
        _menuHandle = Native.CreatePopupMenu();
        uint flags = Native.CMF_NORMAL | Native.CMF_EXPLORE | Native.CMF_CANRENAME | (extended ? Native.CMF_EXTENDEDVERBS : 0);
        if (((IContextMenu)_menu!).QueryContextMenu(_menuHandle, 0, FirstId, LastId, flags) < 0) return [];
        return Read(_menuHandle, depth: 0);
    }

    /// <summary>Reads a Win32 menu into entries. Submenus are filled first, as they would be when the user opens them.</summary>
    private List<ShellMenuEntry> Read(IntPtr menu, int depth)
    {
        var entries = new List<ShellMenuEntry>();
        int count = Native.GetMenuItemCount(menu);
        for (int index = 0; index < count; index++)
        {
            var info = new Native.MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<Native.MENUITEMINFO>(),
                fMask = Native.MIIM_FTYPE | Native.MIIM_STATE | Native.MIIM_ID | Native.MIIM_SUBMENU | Native.MIIM_STRING | Native.MIIM_BITMAP,
            };
            if (!Native.GetMenuItemInfo(menu, (uint)index, true, ref info)) continue;
            if ((info.fType & Native.MFT_SEPARATOR) != 0)
            {
                entries.Add(new ShellMenuEntry(0, "", null, false, true, null, []));
                continue;
            }
            string text = ReadText(menu, index, info.cch);
            IReadOnlyList<ShellMenuEntry> children = [];
            if (info.hSubMenu != IntPtr.Zero && depth < 3)
            {
                FillSubmenu(info.hSubMenu, index);
                children = Read(info.hSubMenu, depth + 1);
            }
            string? verb = info.hSubMenu == IntPtr.Zero && info.wID >= FirstId && info.wID <= LastId ? Verb(info.wID - FirstId) : null;
            ImagePixels? icon = null;
            long bitmap = (long)info.hbmpItem;
            // Values from -1 to 11 are system placeholders (HBMMENU_*), not bitmaps.
            if (bitmap > 11) icon = ShellImages.Read(info.hbmpItem);
            bool enabled = (info.fState & Native.MFS_DISABLED) == 0;
            entries.Add(new ShellMenuEntry(info.wID, Clean(text), verb, enabled, false, icon, children));
        }
        return entries;
    }

    private static string ReadText(IntPtr menu, int index, uint length)
    {
        if (length == 0) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)(length + 1) * 2);
        try
        {
            var info = new Native.MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<Native.MENUITEMINFO>(),
                fMask = Native.MIIM_STRING,
                dwTypeData = buffer,
                cch = length + 1,
            };
            return Native.GetMenuItemInfo(menu, (uint)index, true, ref info) ? Marshal.PtrToStringUni(buffer) ?? "" : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Asks the shell to fill a submenu such as "Send to", which it does only when the submenu is about to open.</summary>
    private void FillSubmenu(IntPtr submenu, int index)
    {
        try
        {
            IntPtr lParam = (IntPtr)(index & 0xFFFF);
            if (_menu is IContextMenu3 menu3) menu3.HandleMenuMsg2(Native.WM_INITMENUPOPUP, submenu, lParam, out _);
            else if (_menu is IContextMenu2 menu2) menu2.HandleMenuMsg(Native.WM_INITMENUPOPUP, submenu, lParam);
        }
        catch (COMException)
        {
        }
    }

    private string? Verb(uint offset)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (((IContextMenu)_menu!).GetCommandString((UIntPtr)offset, Native.GCS_VERBW, IntPtr.Zero, buffer, 256) != 0) return null;
            string? verb = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(verb) ? null : verb;
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

    /// <summary>Removes the keyboard marks (&amp;) and the shortcut text after a tab from a menu text.</summary>
    internal static string Clean(string text)
    {
        int tab = text.IndexOf('\t');
        if (tab >= 0) text = text[..tab];
        var builder = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    builder.Append('&');
                    i++;
                }
                continue;
            }
            builder.Append(text[i]);
        }
        return builder.ToString().Trim();
    }

    /// <summary>Runs a command of the menu on the menu's thread.</summary>
    public void Invoke(uint id, IntPtr owner, Native.POINT point)
    {
        // Some commands finish after InvokeCommand returns; the thread stays a minute to serve them.
        Interlocked.Exchange(ref _stopAtTicks, DateTime.UtcNow.AddMinutes(1).Ticks);
        _work.Add(() =>
        {
            var invoke = new Native.CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE | 0x00100000 /* CMIC_MASK_ASYNCOK */,
                hwnd = owner,
                lpVerb = (IntPtr)(id - FirstId),
                lpVerbW = (IntPtr)(id - FirstId),
                nShow = 1,
                ptInvoke = point,
            };
            ((IContextMenu)_menu!).InvokeCommand(ref invoke);
        });
    }

    /// <summary>The menu was closed. Without a chosen command the thread ends after a short grace time.</summary>
    public void Close()
    {
        long soon = DateTime.UtcNow.AddSeconds(2).Ticks;
        long current = Interlocked.Read(ref _stopAtTicks);
        if (current == long.MaxValue) Interlocked.CompareExchange(ref _stopAtTicks, soon, current);
        _stopping = true;
    }

    private void Release()
    {
        if (_menuHandle != IntPtr.Zero) Native.DestroyMenu(_menuHandle);
        _menuHandle = IntPtr.Zero;
        Native.Release(_menu);
        Native.Release(_folder);
        _items?.Dispose();
        _menu = null;
        _folder = null;
    }
}
