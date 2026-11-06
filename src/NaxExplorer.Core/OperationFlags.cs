namespace NaxExplorer.Core;

/// <summary>
/// The flags handed to the Windows copy engine (IFileOperation) for each kind of operation. They decide whether an
/// operation can be undone, whether deleted items go to the Recycle Bin and whether Windows shows any window of its own.
/// The app shows its own progress, conflict and delete windows, so Windows is asked to stay silent; only the UAC
/// prompt for administrator rights, and the warning before an item that cannot go to the Recycle Bin is deleted for
/// good, remain Windows' own.
/// </summary>
public static class OperationFlags
{
    public const uint Silent = 0x0004;
    public const uint RenameOnCollision = 0x0008;
    public const uint NoConfirmation = 0x0010;
    public const uint AllowUndo = 0x0040;
    public const uint NoConfirmMakeFolder = 0x0200;
    public const uint NoErrorUi = 0x0400;
    public const uint WantNukeWarning = 0x4000;
    public const uint ShowElevationPrompt = 0x00040000;
    public const uint RecycleOnDelete = 0x00080000;
    public const uint AddUndoRecord = 0x20000000;

    private const uint Quiet = Silent | NoErrorUi | ShowElevationPrompt;

    /// <summary>
    /// Copy or move, undoable and without Windows' progress window. <paramref name="replace"/> answers every conflict
    /// with "replace", which is only passed after the user chose that in the app's conflict window;
    /// <paramref name="renameOnCollision"/> keeps both items by giving the new one a free name. With neither, a conflict
    /// that appears only after the app looked still brings up Windows' own question rather than replacing anything.
    /// </summary>
    public static uint ForTransfer(bool renameOnCollision, bool replace = false)
    {
        uint flags = AllowUndo | NoConfirmMakeFolder | AddUndoRecord | Quiet;
        if (renameOnCollision) flags |= RenameOnCollision;
        if (replace) flags |= NoConfirmation;
        return flags;
    }

    /// <summary>
    /// Delete, after the user confirmed it in the app. Normally to the Recycle Bin, and an item that cannot go there
    /// still brings up Windows' warning before it is deleted for good. A permanent delete leaves out "allow undo",
    /// which is what makes Windows delete for good.
    /// </summary>
    public static uint ForDelete(bool permanently) =>
        permanently
            ? Quiet | NoConfirmation
            : Quiet | NoConfirmation | AllowUndo | RecycleOnDelete | AddUndoRecord | WantNukeWarning;

    /// <summary>Rename, undoable. The app checks first that the new name is free, so Windows is never asked to replace.</summary>
    public static uint ForRename() => AllowUndo | AddUndoRecord | Quiet;
}
