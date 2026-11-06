using System.Runtime.InteropServices;

namespace NaxExplorer.Shell;

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    [PreserveSig] int get_Count(out int count);
    [PreserveSig] int Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object? folder);
    [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
    [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int windowClass, out int cookie);
    [PreserveSig] int RegisterPending(int threadId, ref object location, ref object root, int windowClass, out int cookie);
    [PreserveSig] int Revoke(int cookie);
    [PreserveSig] int OnNavigate(int cookie, ref object location);
    [PreserveSig] int OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
}

/// <summary>The browser interface of a folder window (IWebBrowserApp), in vtable order after IDispatch.</summary>
[ComImport, Guid("0002DF05-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWebBrowserApp
{
    [PreserveSig] int GoBack();
    [PreserveSig] int GoForward();
    [PreserveSig] int GoHome();
    [PreserveSig] int GoSearch();
    [PreserveSig] int Navigate(IntPtr url, IntPtr flags, IntPtr frame, IntPtr postData, IntPtr headers);
    [PreserveSig] int Refresh();
    [PreserveSig] int Refresh2(IntPtr level);
    [PreserveSig] int Stop();
    [PreserveSig] int get_Application(out IntPtr value);
    [PreserveSig] int get_Parent(out IntPtr value);
    [PreserveSig] int get_Container(out IntPtr value);
    [PreserveSig] int get_Document(out IntPtr value);
    [PreserveSig] int get_TopLevelContainer(out short value);
    [PreserveSig] int get_Type(out IntPtr value);
    [PreserveSig] int get_Left(out int value);
    [PreserveSig] int put_Left(int value);
    [PreserveSig] int get_Top(out int value);
    [PreserveSig] int put_Top(int value);
    [PreserveSig] int get_Width(out int value);
    [PreserveSig] int put_Width(int value);
    [PreserveSig] int get_Height(out int value);
    [PreserveSig] int put_Height(int value);
    [PreserveSig] int get_LocationName(out IntPtr value);
    [PreserveSig] int get_LocationURL(out IntPtr value);
    [PreserveSig] int get_Busy(out short value);
    [PreserveSig] int Quit();
    [PreserveSig] int ClientToWindow(ref int width, ref int height);
    [PreserveSig] int PutProperty(IntPtr name, IntPtr value);
    [PreserveSig] int GetProperty(IntPtr name, IntPtr value);
    [PreserveSig] int get_Name(out IntPtr value);
    [PreserveSig] int get_HWND(out IntPtr value);
    [PreserveSig] int get_FullName(out IntPtr value);
    [PreserveSig] int get_Path(out IntPtr value);
    [PreserveSig] int get_Visible(out short value);
    [PreserveSig] int put_Visible(short value);
    [PreserveSig] int get_StatusBar(out short value);
    [PreserveSig] int put_StatusBar(short value);
    [PreserveSig] int get_StatusText(out IntPtr value);
    [PreserveSig] int put_StatusText(IntPtr value);
    [PreserveSig] int get_ToolBar(out int value);
    [PreserveSig] int put_ToolBar(int value);
    [PreserveSig] int get_MenuBar(out short value);
    [PreserveSig] int put_MenuBar(short value);
    [PreserveSig] int get_FullScreen(out short value);
    [PreserveSig] int put_FullScreen(short value);
}

/// <summary>The folder view of a folder window (IShellFolderViewDual), in vtable order after IDispatch.</summary>
[ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellFolderViewDual
{
    [PreserveSig] int get_Application(out IntPtr value);
    [PreserveSig] int get_Parent(out IntPtr value);
    [PreserveSig] int get_Folder(out IntPtr value);
    [PreserveSig] int SelectedItems(out IntPtr value);
    [PreserveSig] int get_FocusedItem(out IntPtr value);
    [PreserveSig] int SelectItem(IntPtr item, int flags);
    [PreserveSig] int PopupItemMenu(IntPtr item, IntPtr x, IntPtr y, out IntPtr command);
    [PreserveSig] int get_Script(out IntPtr value);
    [PreserveSig] int get_ViewOptions(out int value);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
    [PreserveSig] int ContextSensitiveHelp(int enter);
    [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
    [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr ole, IntPtr active);
    [PreserveSig] int RemoveMenusSB(IntPtr menu);
    [PreserveSig] int SetStatusTextSB(IntPtr text);
    [PreserveSig] int EnableModelessSB(int enable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
    [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
    [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    [PreserveSig] int QueryActiveShellView(out IntPtr view);
    [PreserveSig] int OnViewWindowActive(IntPtr view);
    [PreserveSig] int SetToolbarItems(IntPtr buttons, uint count, uint flags);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
    [PreserveSig] int ContextSensitiveHelp(int enter);
    [PreserveSig] int TranslateAccelerator(IntPtr message);
    [PreserveSig] int EnableModeless(int enable);
    [PreserveSig] int UIActivate(uint state);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(IntPtr settings);
    [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(IntPtr pidlItem, uint flags);
    [PreserveSig] int GetItemObject(uint item, ref Guid riid, out IntPtr result);
}

/// <summary>
/// Stands for one app window in Windows' list of open folder windows. "Show in folder" in other programs
/// (SHOpenFolderAndSelectItems) looks for a window of the folder in that list and asks it to select the file through
/// its browser (<see cref="IWebBrowserApp"/>), its view and finally <see cref="IShellView.SelectItem"/>; the request is handed to the window. Everything else a folder window could be
/// asked is answered with "not supported".
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class ShellWindowHost : IServiceProvider, IShellBrowser, IShellView, IWebBrowserApp, IShellFolderViewDual
{
    int IWebBrowserApp.get_HWND(out IntPtr value)
    {
        value = _hwnd;
        return 0;
    }

    int IWebBrowserApp.get_Visible(out short value)
    {
        value = -1;
        return 0;
    }

    int IWebBrowserApp.get_Busy(out short value)
    {
        value = 0;
        return 0;
    }

    int IWebBrowserApp.get_Document(out IntPtr value)
    {
        value = Marshal.GetComInterfaceForObject<ShellWindowHost, IShellFolderViewDual>(this);
        return 0;
    }

    int IShellFolderViewDual.SelectItem(IntPtr item, int flags)
    {
        // The item comes as a VARIANT; Windows passes the item's identity relative to the folder as a byte array.
        if (item == IntPtr.Zero) return 0;
        try
        {
            object? value = Marshal.GetObjectForNativeVariant(item);
            if (value is byte[] bytes && bytes.Length > 0)
            {
                IntPtr child = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, child, bytes.Length);
                    return ((IShellView)this).SelectItem(child, (uint)flags);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(child);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or COMException or InvalidOleVariantTypeException)
        {
            // An item the window cannot read is simply not selected; the folder is open either way.
        }
        return 0;
    }

    int IShellFolderViewDual.get_Application(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.get_Parent(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.get_Folder(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.SelectedItems(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.get_FocusedItem(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.PopupItemMenu(IntPtr item, IntPtr x, IntPtr y, out IntPtr command) => Nothing(out command);
    int IShellFolderViewDual.get_Script(out IntPtr value) => Nothing(out value);
    int IShellFolderViewDual.get_ViewOptions(out int value) => Zero(out value);

    int IWebBrowserApp.GoBack() => NotImplemented;
    int IWebBrowserApp.GoForward() => NotImplemented;
    int IWebBrowserApp.GoHome() => NotImplemented;
    int IWebBrowserApp.GoSearch() => NotImplemented;
    int IWebBrowserApp.Navigate(IntPtr url, IntPtr flags, IntPtr frame, IntPtr postData, IntPtr headers) => NotImplemented;
    int IWebBrowserApp.Refresh() => NotImplemented;
    int IWebBrowserApp.Refresh2(IntPtr level) => NotImplemented;
    int IWebBrowserApp.Stop() => NotImplemented;
    int IWebBrowserApp.get_Application(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_Parent(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_Container(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_TopLevelContainer(out short value)
    {
        value = -1;
        return 0;
    }
    int IWebBrowserApp.get_Type(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_Left(out int value) => Zero(out value);
    int IWebBrowserApp.put_Left(int value) => NotImplemented;
    int IWebBrowserApp.get_Top(out int value) => Zero(out value);
    int IWebBrowserApp.put_Top(int value) => NotImplemented;
    int IWebBrowserApp.get_Width(out int value) => Zero(out value);
    int IWebBrowserApp.put_Width(int value) => NotImplemented;
    int IWebBrowserApp.get_Height(out int value) => Zero(out value);
    int IWebBrowserApp.put_Height(int value) => NotImplemented;
    int IWebBrowserApp.get_LocationName(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_LocationURL(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.Quit() => NotImplemented;
    int IWebBrowserApp.ClientToWindow(ref int width, ref int height) => NotImplemented;
    int IWebBrowserApp.PutProperty(IntPtr name, IntPtr value) => NotImplemented;
    int IWebBrowserApp.GetProperty(IntPtr name, IntPtr value) => NotImplemented;
    int IWebBrowserApp.get_Name(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_FullName(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.get_Path(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.put_Visible(short value)
    {
        return 0;
    }
    int IWebBrowserApp.get_StatusBar(out short value)
    {
        value = 0;
        return 0;
    }
    int IWebBrowserApp.put_StatusBar(short value) => NotImplemented;
    int IWebBrowserApp.get_StatusText(out IntPtr value) => Nothing(out value);
    int IWebBrowserApp.put_StatusText(IntPtr value) => NotImplemented;
    int IWebBrowserApp.get_ToolBar(out int value) => Zero(out value);
    int IWebBrowserApp.put_ToolBar(int value) => NotImplemented;
    int IWebBrowserApp.get_MenuBar(out short value)
    {
        value = 0;
        return 0;
    }
    int IWebBrowserApp.put_MenuBar(short value) => NotImplemented;
    int IWebBrowserApp.get_FullScreen(out short value)
    {
        value = 0;
        return 0;
    }
    int IWebBrowserApp.put_FullScreen(short value) => NotImplemented;

    private static int Nothing(out IntPtr value)
    {
        value = IntPtr.Zero;
        return NotImplemented;
    }

    private static int Zero(out int value)
    {
        value = 0;
        return 0;
    }


    private const int NotImplemented = unchecked((int)0x80004001);
    private const int NoInterface = unchecked((int)0x80004002);

    private readonly IntPtr _hwnd;
    private readonly Action<string, bool> _select;

    /// <summary>The folder the window shows, as Windows identifies it, to turn a selected child into a full name.</summary>
    internal IntPtr FolderPidl { get; set; }

    /// <param name="select">Called with the full name of an item to select, and whether the others are deselected first.</param>
    internal ShellWindowHost(IntPtr hwnd, Action<string, bool> select)
    {
        _hwnd = hwnd;
        _select = select;
    }

    int IServiceProvider.QueryService(ref Guid service, ref Guid riid, out IntPtr result)
    {
        result = IntPtr.Zero;
        // Windows asks for the browser and the view under several service names; this one object is all of them.
        IntPtr unknown = Marshal.GetIUnknownForObject(this);
        try
        {
            return Marshal.QueryInterface(unknown, in riid, out result);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    int IShellBrowser.GetWindow(out IntPtr hwnd)
    {
        hwnd = _hwnd;
        return 0;
    }

    int IShellBrowser.QueryActiveShellView(out IntPtr view)
    {
        view = Marshal.GetComInterfaceForObject<ShellWindowHost, IShellView>(this);
        return 0;
    }

    int IShellView.GetWindow(out IntPtr hwnd)
    {
        hwnd = _hwnd;
        return 0;
    }

    int IShellView.SelectItem(IntPtr pidlItem, uint flags)
    {
        const uint Select = 0x1, DeselectOthers = 0x4;
        if (pidlItem == IntPtr.Zero || FolderPidl == IntPtr.Zero) return 0;
        IntPtr full = Native.ILCombine(FolderPidl, pidlItem);
        if (full == IntPtr.Zero) return 0;
        try
        {
            string? name = NameOf(full, 0x80058000 /* SIGDN_FILESYSPATH */) ?? NameOf(full, 0x80028000 /* SIGDN_DESKTOPABSOLUTEPARSING */);
            if (name is not null && (flags & Select) != 0) _select(name, (flags & DeselectOthers) != 0);
        }
        finally
        {
            Native.ILFree(full);
        }
        return 0;
    }

    private static string? NameOf(IntPtr pidl, uint sigdn)
    {
        if (Native.SHGetNameFromIDList(pidl, sigdn, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    int IShellBrowser.ContextSensitiveHelp(int enter) => NotImplemented;
    int IShellBrowser.InsertMenusSB(IntPtr menu, IntPtr widths) => NotImplemented;
    int IShellBrowser.SetMenuSB(IntPtr menu, IntPtr ole, IntPtr active) => NotImplemented;
    int IShellBrowser.RemoveMenusSB(IntPtr menu) => NotImplemented;
    int IShellBrowser.SetStatusTextSB(IntPtr text) => NotImplemented;
    int IShellBrowser.EnableModelessSB(int enable) => NotImplemented;
    int IShellBrowser.TranslateAcceleratorSB(IntPtr message, ushort id) => NotImplemented;
    int IShellBrowser.BrowseObject(IntPtr pidl, uint flags) => NotImplemented;
    int IShellBrowser.GetViewStateStream(uint mode, out IntPtr stream)
    {
        stream = IntPtr.Zero;
        return NotImplemented;
    }
    int IShellBrowser.GetControlWindow(uint id, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        return NotImplemented;
    }
    int IShellBrowser.SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return NotImplemented;
    }
    int IShellBrowser.OnViewWindowActive(IntPtr view) => NotImplemented;
    int IShellBrowser.SetToolbarItems(IntPtr buttons, uint count, uint flags) => NotImplemented;
    int IShellView.ContextSensitiveHelp(int enter) => NotImplemented;
    int IShellView.TranslateAccelerator(IntPtr message) => 1;
    int IShellView.EnableModeless(int enable) => NotImplemented;
    int IShellView.UIActivate(uint state) => 0;
    int IShellView.Refresh() => 0;
    int IShellView.CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        return NotImplemented;
    }
    int IShellView.DestroyViewWindow() => NotImplemented;
    int IShellView.GetCurrentInfo(IntPtr settings) => NotImplemented;
    int IShellView.AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam) => NotImplemented;
    int IShellView.SaveViewState() => NotImplemented;
    int IShellView.GetItemObject(uint item, ref Guid riid, out IntPtr result)
    {
        result = IntPtr.Zero;
        return NotImplemented;
    }
}

/// <summary>
/// Keeps one app window in Windows' list of open folder windows while it is open, with the folder its active pane
/// shows. Windows compares the folder's identity byte for byte, and programs build that identity in one of two forms:
/// from the path (ILCreateFromPath) or by parsing its name (SHParseDisplayName), which differ in their bytes. So the
/// window is listed twice, once in each form; the second entry belongs to a hidden helper window of the same thread,
/// and both lead to the same window. A failure only means "Show in folder" opens the folder without selecting the
/// file, so it is logged and never stops the app. Must be used on the window's UI thread.
/// </summary>
internal sealed class ShellWindowRegistration : IDisposable
{
    private const int SwcBrowser = 1;
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private readonly ShellWindowHost _host;
    private readonly Entry _fromPath;
    private readonly Entry _parsed;
    private IShellWindows? _windows;
    private IntPtr _pidl;
    private IntPtr _helper;
    private bool _failed;

    /// <summary>One entry in Windows' list: the window it names and the cookie Windows gave it.</summary>
    private sealed class Entry(IntPtr hwnd)
    {
        public IntPtr Hwnd { get; set; } = hwnd;

        public int Cookie { get; set; }
    }

    public ShellWindowRegistration(IntPtr hwnd, Action<string, bool> select)
    {
        _host = new ShellWindowHost(hwnd, select);
        _fromPath = new Entry(hwnd);
        _parsed = new Entry(IntPtr.Zero);
    }

    /// <summary>Tells Windows which folder the window shows now: a path, a shell place or This PC.</summary>
    public void Navigated(string location)
    {
        if (_failed) return;
        string parsing = Core.PathTools.IsThisPc(location) ? "::" + Core.ShellPlaces.ThisPcClsid : location;
        if (Native.SHParseDisplayName(parsing, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
        try
        {
            _windows ??= (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!)!;
            // The parsed form exists for every place; the path form only for folders on disk.
            if (_parsed.Hwnd == IntPtr.Zero)
            {
                _helper = Native.CreateWindowEx(0, "STATIC", "Nax Explorer", 0, 0, 0, 0, 0, _fromPath.Hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _parsed.Hwnd = _helper;
            }
            Announce(_parsed, PidlBytes(pidl));
            IntPtr fromPath = Core.PathTools.IsShellPlace(location) || Core.PathTools.IsThisPc(location) ? IntPtr.Zero : Native.ILCreateFromPath(location);
            if (fromPath != IntPtr.Zero)
            {
                try
                {
                    Announce(_fromPath, PidlBytes(fromPath));
                }
                finally
                {
                    Native.ILFree(fromPath);
                }
            }
            else if (_fromPath.Cookie != 0)
            {
                // Virtual locations have no filesystem identity. Do not leave this window registered under
                // the folder it used to show, or a later selection request would use the wrong folder PIDL.
                _windows.Revoke(_fromPath.Cookie);
                _fromPath.Cookie = 0;
            }
            if (_pidl != IntPtr.Zero) Native.ILFree(_pidl);
            _pidl = pidl;
            _host.FolderPidl = pidl;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Native.ILFree(pidl);
            Fail(ex);
        }
    }

    /// <summary>Lists an entry with its folder, or moves it to the new folder when it is listed already.</summary>
    private void Announce(Entry entry, object where)
    {
        if (_windows is null || entry.Hwnd == IntPtr.Zero) return;
        if (entry.Cookie != 0)
        {
            // "Show in folder" waits for a window to be registered for the folder it opened, not for one that moves
            // there, so a new folder means a new registration.
            _windows.Revoke(entry.Cookie);
            entry.Cookie = 0;
        }
        object root = null!;
        int threadId = (int)Native.GetWindowThreadProcessId(entry.Hwnd, out _);
        int pending = _windows.RegisterPending(threadId, ref where, ref root, SwcBrowser, out int pendingCookie);
        int finalCookie = 0;
        int registered = pending == 0 ? _windows.Register(_host, (int)entry.Hwnd, SwcBrowser, out finalCookie) : pending;
        if (registered == 0)
        {
            entry.Cookie = finalCookie;
            return;
        }
        // A pending entry that was never completed is taken back, so no stale entry stays in Windows' list.
        if (pending == 0 && pendingCookie != 0) _windows.Revoke(pendingCookie);
        CrashLog.Write(new COMException("Listing the window as a folder window failed.", registered));
    }

    /// <summary>Tells Windows the window became the active one, so "Show in folder" prefers it.</summary>
    public void Activated(bool active)
    {
        if (_windows is null) return;
        foreach (Entry entry in new[] { _fromPath, _parsed })
        {
            if (entry.Cookie == 0) continue;
            try
            {
                _windows.OnActivated(entry.Cookie, active);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Only the preference among several windows is lost.
            }
        }
    }

    private void Fail(Exception ex)
    {
        _failed = true;
        CrashLog.Write(ex);
        Dispose();
    }

    private static byte[] PidlBytes(IntPtr pidl)
    {
        var bytes = new byte[Native.ILGetSize(pidl)];
        Marshal.Copy(pidl, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        foreach (Entry entry in new[] { _fromPath, _parsed })
        {
            try
            {
                if (_windows is not null && entry.Cookie != 0) _windows.Revoke(entry.Cookie);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Windows drops the entry itself once the window is gone.
            }
            entry.Cookie = 0;
        }
        if (_windows is not null) Native.Release(_windows);
        _windows = null;
        if (_helper != IntPtr.Zero) Native.DestroyWindow(_helper);
        _helper = IntPtr.Zero;
        _parsed.Hwnd = IntPtr.Zero;
        _host.FolderPidl = IntPtr.Zero;
        if (_pidl != IntPtr.Zero) Native.ILFree(_pidl);
        _pidl = IntPtr.Zero;
    }
}

