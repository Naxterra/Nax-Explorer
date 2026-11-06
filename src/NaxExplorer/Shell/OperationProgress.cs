using System.Runtime.InteropServices;

namespace NaxExplorer.Shell;

/// <summary>The copy engine's progress callbacks, in vtable order.</summary>
[ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? created);
    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
    [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
    [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
    [PreserveSig] int UpdateProgress(uint total, uint done);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

/// <summary>One failed item of an operation and the reason Windows gave.</summary>
internal sealed record OperationFailure(string Name, string Message);

/// <summary>
/// Follows one operation for the app's own progress window: the share done, the item being worked on and the items that
/// failed. A cancel request is answered at the engine's next callback, which stops the operation; what was already
/// copied stays, as with Windows' own Cancel button. The callbacks run on the operation's own thread.
/// </summary>
internal sealed class OperationProgress : IFileOperationProgressSink
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private readonly List<OperationFailure> _failures = [];
    private volatile bool _cancel;

    /// <summary>Raised on the operation's thread with the share done, from 0 to 1, and the current item's name.</summary>
    public event Action<double, string?>? Changed;

    public IReadOnlyList<OperationFailure> Failures
    {
        get
        {
            lock (_failures) return _failures.ToList();
        }
    }

    public bool IsCancelled => _cancel;

    public void Cancel() => _cancel = true;

    private string? _current;
    private double _share;

    /// <summary>The batches of one request run one after another; each covers its own part of the whole bar.</summary>
    public void BeginBatch(int index, int count)
    {
        _batchStart = count == 0 ? 0 : (double)index / count;
        _batchSize = count == 0 ? 1 : 1.0 / count;
    }

    private double _batchStart;
    private double _batchSize = 1;

    private int Step(IShellItem? item)
    {
        if (item is not null) _current = NameOf(item);
        Changed?.Invoke(_share, _current);
        return _cancel ? Cancelled : 0;
    }

    private int After(IShellItem item, int result)
    {
        // Success codes such as "skipped" are no failure, and a cancel is not reported per item.
        if (result < 0 && result != Cancelled && result != unchecked((int)0x80270000))
        {
            string message = Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}";
            lock (_failures) _failures.Add(new OperationFailure(NameOf(item) ?? "?", message));
        }
        return _cancel ? Cancelled : 0;
    }

    private static string? NameOf(IShellItem item)
    {
        if (item.GetDisplayName(0x00000000 /* SIGDN_NORMALDISPLAY */, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    public int StartOperations() => _cancel ? Cancelled : 0;
    public int FinishOperations(int result) => 0;
    public int PreRenameItem(uint flags, IShellItem item, string? newName) => Step(item);
    public int PostRenameItem(uint flags, IShellItem item, string? newName, int result, IShellItem? created) => After(item, result);
    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => Step(item);
    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => After(item, result);
    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => Step(item);
    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => After(item, result);
    public int PreDeleteItem(uint flags, IShellItem item) => Step(item);
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) => After(item, result);
    public int PreNewItem(uint flags, IShellItem destination, string? newName) => _cancel ? Cancelled : 0;
    public int PostNewItem(uint flags, IShellItem destination, string? newName, string? template, uint attributes, int result, IShellItem? created) => 0;

    public int UpdateProgress(uint total, uint done)
    {
        _share = _batchStart + (total == 0 ? 0 : Math.Clamp((double)done / total, 0, 1)) * _batchSize;
        return Step(null);
    }

    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
