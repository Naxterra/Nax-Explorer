namespace NaxExplorer.Core;

/// <summary>
/// The flags handed to the Windows copy engine (IFileOperation) for each kind of operation. They decide whether an
/// operation can be undone, whether deleted items go to the Recycle Bin and whether Windows asks first.
/// </summary>
public static class OperationFlags
{
    public const uint RenameOnCollision = 0x0008;
    public const uint AllowUndo = 0x0040;
    public const uint NoConfirmMakeFolder = 0x0200;
    public const uint ShowElevationPrompt = 0x00040000;
    public const uint RecycleOnDelete = 0x00080000;
    public const uint AddUndoRecord = 0x20000000;

    /// <summary>Copy or move: undoable, and with "- Copy" names instead of a conflict dialog when asked for.</summary>
    public static uint ForTransfer(bool renameOnCollision) =>
        AllowUndo | NoConfirmMakeFolder | AddUndoRecord | ShowElevationPrompt | (renameOnCollision ? RenameOnCollision : 0);

    /// <summary>
    /// Delete. Normally to the Recycle Bin. A permanent delete leaves out "allow undo", which is what makes Windows
    /// delete for good, and it never suppresses the confirmation, so Windows always asks first.
    /// </summary>
    public static uint ForDelete(bool permanently) =>
        permanently ? ShowElevationPrompt : AllowUndo | RecycleOnDelete | AddUndoRecord | ShowElevationPrompt;

    public static uint ForRename() => AllowUndo | AddUndoRecord | ShowElevationPrompt;
}
