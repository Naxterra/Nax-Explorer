namespace NaxExplorer.Core;

/// <summary>Leaves clicks with the list until movement commits to a selection box.</summary>
public sealed class SelectionGesture
{
    private (double X, double Y)? _origin;
    public bool Dragging { get; private set; }

    public void Press(double x, double y)
    {
        _origin = (x, y);
        Dragging = false;
    }

    public bool Move(double x, double y)
    {
        if (_origin is not { } origin || Dragging) return false;
        if (Math.Abs(x - origin.X) < 4 && Math.Abs(y - origin.Y) < 4) return false;
        Dragging = true;
        return true;
    }

    public void Reset()
    {
        _origin = null;
        Dragging = false;
    }
}
