namespace NaxExplorer.Core;

public readonly record struct FileDropChoice(string Target, bool Move);

/// <summary>A mouse drag captures its source once and chooses the action at release.</summary>
public sealed class FileDragSession(IEnumerable<string> sources)
{
    private bool _hovered, _control, _shift;
    public IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(sources.ToArray());
    public string? Target { get; private set; }
    public FileDropChoice? Choice { get; private set; }
    public bool Active { get; private set; } = true;

    public void Hover(string? target, bool control, bool shift)
    {
        if (!Active) return;
        if (_hovered && string.Equals(Target, target, StringComparison.OrdinalIgnoreCase) && _control == control && _shift == shift) return;
        _hovered = true;
        _control = control;
        _shift = shift;
        Target = target;
        Choice = null;
        if (Sources.Count == 0 || target is null || PathTools.IsThisPc(target) || PathTools.IsShellPlace(target) || Sources.Any(PathTools.IsShellPlace)) return;
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
        Hover(Target, control, shift);
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
