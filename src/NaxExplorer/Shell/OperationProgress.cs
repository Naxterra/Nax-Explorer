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
    private sealed record PendingTransfer(string? ExpectedTarget, bool IsMove, bool ExistedBefore, bool RenameOnCollision, OperationFailure Failure);

    private const int Cancelled = unchecked((int)0x800704C7);
    private const int RequiresElevation = unchecked((int)0x80270002); // COPYENGINE_E_REQUIRES_ELEVATION
    private readonly List<OperationFailure> _failures = [];
    private readonly Dictionary<string, OperationFailure> _pendingDeleteElevation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingTransfer> _pendingTransferElevation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _targetExistedBefore = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _cancel;
    private bool _batchRenameOnCollision;

    /// <summary>Raised on the operation's thread with the share done, from 0 to 1, and the current item's name.</summary>
    public event Action<double, string?>? Changed;

    public IReadOnlyList<OperationFailure> Failures
    {
        get
        {
            lock (_failures) return (OperationFailure[])[.. _failures, .. _pendingDeleteElevation.Values, .. _pendingTransferElevation.Values.Select(v => v.Failure)];
        }
    }

    public bool IsCancelled => _cancel;

    public void Cancel() => _cancel = true;

    private string? _current;
    private double _share;

    /// <summary>The batches of one request run one after another; each covers its own part of the whole bar.</summary>
    public void BeginBatch(int index, int count, bool move = false, bool renameOnCollision = false)
    {
        _ = move;
        _batchStart = count == 0 ? 0 : (double)index / count;
        _batchSize = count == 0 ? 1 : 1.0 / count;
        _batchRenameOnCollision = renameOnCollision;
        lock (_failures) _targetExistedBefore.Clear();
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
            lock (_failures) _failures.Add(new OperationFailure(NameOf(item) ?? "?", DescribeShellFailure(result)));
        }
        return _cancel ? Cancelled : 0;
    }

    internal static string DescribeShellFailure(int result) => result == RequiresElevation
        ? Loc.T("OperationRequiresAdministrator")
        : Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}";

    // The shell can report this result more than once while arranging an elevated retry. Keep one entry for the
    // exact path and decide whether it was a failure only after Windows has finished the delete.
    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
    {
        if (result == RequiresElevation && NameOf(item, 0x80058000 /* SIGDN_FILESYSPATH */) is { } path)
        {
            lock (_failures) _pendingDeleteElevation.TryAdd(path, new OperationFailure(NameOf(item) ?? path, DescribeShellFailure(result)));
            return _cancel ? Cancelled : 0;
        }
        if (result >= 0 && NameOf(item, 0x80058000 /* SIGDN_FILESYSPATH */) is { } okPath)
        {
            lock (_failures) _pendingDeleteElevation.Remove(okPath);
        }
        return After(item, result);
    }

    private int BeforeTransfer(IShellItem item, IShellItem destination, string? newName)
    {
        if (NameOf(item, 0x80058000 /* SIGDN_FILESYSPATH */) is { } sourcePath
            && ExpectedTargetOf(sourcePath, destination, newName) is { } expectedTarget)
        {
            lock (_failures) _targetExistedBefore.TryAdd(sourcePath, Path.Exists(expectedTarget));
        }
        return Step(item);
    }

    private static string? ExpectedTargetOf(string sourcePath, IShellItem destination, string? newName)
    {
        string? targetFolder = NameOf(destination, 0x80058000 /* SIGDN_FILESYSPATH */);
        string? itemName = string.IsNullOrEmpty(newName) ? Path.GetFileName(sourcePath.TrimEnd('\\')) : newName;
        return !string.IsNullOrEmpty(targetFolder) && !string.IsNullOrEmpty(itemName)
            ? Path.Combine(targetFolder, itemName)
            : null;
    }

    private int AfterTransfer(IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created, bool isMove)
    {
        string? sourcePath = NameOf(item, 0x80058000 /* SIGDN_FILESYSPATH */);
        if (result == RequiresElevation && sourcePath is not null)
        {
            string? expectedTarget = ExpectedTargetOf(sourcePath, destination, newName);
            lock (_failures)
            {
                bool existedBefore = _targetExistedBefore.TryGetValue(sourcePath, out bool existed)
                    ? existed
                    : expectedTarget is not null && Path.Exists(expectedTarget);
                _pendingTransferElevation.TryAdd(sourcePath,
                    new PendingTransfer(expectedTarget, isMove, existedBefore, _batchRenameOnCollision,
                        new OperationFailure(NameOf(item) ?? sourcePath, DescribeShellFailure(result))));
            }
            return _cancel ? Cancelled : 0;
        }
        if (result >= 0 && sourcePath is not null)
        {
            lock (_failures) _pendingTransferElevation.Remove(sourcePath);
        }
        return After(item, result);
    }

    internal void FinishDelete()
    {
        if (_cancel)
        {
            lock (_failures) _pendingDeleteElevation.Clear();
            return;
        }
        string[] paths;
        lock (_failures) paths = [.. _pendingDeleteElevation.Keys];
        foreach (string path in paths)
        {
            if (IsConfirmedMissing(path))
            {
                lock (_failures) _pendingDeleteElevation.Remove(path);
            }
        }
    }

    internal void FinishTransfer()
    {
        if (_cancel)
        {
            lock (_failures) _pendingTransferElevation.Clear();
            return;
        }
        KeyValuePair<string, PendingTransfer>[] pending;
        lock (_failures) pending = [.. _pendingTransferElevation];
        foreach (var (sourcePath, entry) in pending)
        {
            // For a move, the source vanishing confirms the elevated retry ran. For a copy into a free target name,
            // the target appearing confirms it. When a target already existed (Replace or KeepBoth), rely on the
            // shell's success callback rather than Path.Exists alone so a failed elevation is never hidden.
            bool succeeded = entry.IsMove
                ? IsConfirmedMissing(sourcePath) && (entry.ExpectedTarget is null || Path.Exists(entry.ExpectedTarget))
                : !entry.ExistedBefore && !entry.RenameOnCollision && entry.ExpectedTarget is not null && Path.Exists(entry.ExpectedTarget);
            if (succeeded)
            {
                lock (_failures) _pendingTransferElevation.Remove(sourcePath);
            }
        }
    }

    private static bool IsConfirmedMissing(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return Directory.Exists(Path.GetPathRoot(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static string? NameOf(IShellItem item, uint format = 0 /* SIGDN_NORMALDISPLAY */)
    {
        if (item.GetDisplayName(format, out IntPtr name) != 0 || name == IntPtr.Zero) return null;
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
    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => BeforeTransfer(item, destination, newName);
    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => AfterTransfer(item, destination, newName, result, created, isMove: true);
    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => BeforeTransfer(item, destination, newName);
    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int result, IShellItem? created) => AfterTransfer(item, destination, newName, result, created, isMove: false);
    public int PreDeleteItem(uint flags, IShellItem item) => Step(item);
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
