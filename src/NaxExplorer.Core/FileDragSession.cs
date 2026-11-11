namespace NaxExplorer.Core;

/// <summary>Where a drag ends: a folder that takes a copy or a move, or the Recycle Bin, which deletes.</summary>
public readonly record struct FileDropChoice(string Target, bool Move, bool Recycle = false);

/// <summary>A mouse drag captures its source once and chooses the action at release.</summary>
public sealed class FileDragSession(IEnumerable<string> sources)
{
    private bool _hovered, _control, _shift, _recycle;
    public IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(sources.ToArray());
    public string? Target { get; private set; }
    public FileDropChoice? Choice { get; private set; }
    public bool Active { get; private set; } = true;

    /// <param name="recycleBin">True when <paramref name="target"/> is the Recycle Bin, where a drop deletes the
    /// items, as in Explorer, whatever keys are held.</param>
    public void Hover(string? target, bool control, bool shift, bool recycleBin = false)
    {
        if (!Active) return;
        if (_hovered && string.Equals(Target, target, StringComparison.OrdinalIgnoreCase) && _control == control && _shift == shift && _recycle == recycleBin) return;
        _hovered = true;
        _control = control;
        _shift = shift;
        _recycle = recycleBin;
        Target = target;
        Choice = null;
        if (Sources.Count == 0 || target is null || Sources.Any(PathTools.IsShellPlace)) return;
        if (recycleBin)
        {
            Choice = new(target, Move: true, Recycle: true);
            return;
        }
        if (PathTools.IsThisPc(target) || PathTools.IsShellPlace(target)) return;
        // Hover checks stay in memory. Probing a directory here could block each
        // pointer update on a slow disk or an unavailable network share.
        if (Sources.Any(source => PathTools.IsSameOrInside(target, source))) return;
        bool move = DropPolicy.Moves(control, shift, Sources, target);
        if (move && Sources.All(source => Path.GetDirectoryName(source.TrimEnd('\\')) is { } parent && PathTools.SameLocation(parent, target))) return;
        Choice = new(target, move);
    }

    public FileDropChoice? Release(bool control, bool shift)
    {
        if (!Active) return null;
        Hover(Target, control, shift, _recycle);
        Active = false;
        return Choice;
    }

    public void Cancel()
    {
        Active = false;
        Target = null;
        Choice = null;
    }
}
