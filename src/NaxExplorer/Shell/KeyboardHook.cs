using System.Runtime.InteropServices;

namespace NaxExplorer.Shell;

/// <summary>
/// A keyboard hook for the app's own UI thread only (not system-wide). WinUI treats F7 as the switch for caret browsing
/// and asks about it before any key event reaches the app, so F7 ("copy to the other pane") is taken here first.
/// </summary>
internal static partial class KeyboardHook
{
    private const int WhKeyboard = 2;
    private const int HcAction = 0;

    private static HookProc? s_proc;
    private static IntPtr s_hook;
    private static int s_swallowedKey;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Installs the hook once. <paramref name="onKeyDown"/> gets the virtual key of each key press and returns true
    /// when it handled the key, which then never reaches WinUI (its release is swallowed too).
    /// </summary>
    public static void Install(Func<int, bool> onKeyDown)
    {
        if (s_hook != IntPtr.Zero) return;
        s_proc = (code, wParam, lParam) =>
        {
            if (code == HcAction)
            {
                long flags = (long)lParam;
                bool released = (flags & 0x80000000) != 0;
                bool repeated = (flags & 0x40000000) != 0;
                int key = (int)wParam;
                if (!released && !repeated && onKeyDown(key))
                {
                    s_swallowedKey = key;
                    return 1;
                }
                if (released && key == s_swallowedKey)
                {
                    s_swallowedKey = 0;
                    return 1;
                }
                if (!released && repeated && key == s_swallowedKey) return 1;
            }
            return CallNextHookEx(s_hook, code, wParam, lParam);
        };
        s_hook = SetWindowsHookEx(WhKeyboard, s_proc, IntPtr.Zero, GetCurrentThreadId());
    }

    public static void Uninstall()
    {
        if (s_hook != IntPtr.Zero) UnhookWindowsHookEx(s_hook);
        s_hook = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
