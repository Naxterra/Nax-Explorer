namespace NaxExplorer.Shell;

/// <summary>
/// A hidden window the app keeps while it runs in the background. Setup, the uninstaller and Windows at sign-out find
/// it by its title and ask it to close, which ends the app cleanly, so no program file stays locked and the app never
/// has to be ended by force.
/// </summary>
internal sealed class BackgroundWindow : IDisposable
{
    /// <summary>The title setup looks for; it must match the installer script.</summary>
    public const string Title = "Nax Explorer Background";

    private const int WmClose = 0x0010;
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    private readonly Native.SubclassProc _proc;
    private IntPtr _hwnd;

    public BackgroundWindow(Action quit)
    {
        _proc = (hwnd, message, wParam, lParam, _, _) =>
        {
            if (message == WmClose || message == WmEndSession && wParam != IntPtr.Zero)
            {
                quit();
                return IntPtr.Zero;
            }
            if (message == WmQueryEndSession) return 1;
            return Native.DefSubclassProc(hwnd, message, wParam, lParam);
        };
        _hwnd = Native.CreateWindowEx(0, "STATIC", Title, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero) Native.SetWindowSubclass(_hwnd, _proc, (UIntPtr)1, UIntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)1);
        Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
