namespace NaxExplorer.Core;

/// <summary>A view reset finishes when the platform reports it, not when it accepts a request.</summary>
public sealed class ViewResetState
{
    public bool Pending { get; private set; }
    public void Request() => Pending = true;
    public void Cancel() => Pending = false;

    public bool Observe(double zoom, double targetZoom, double x, double y)
    {
        if (Pending && Math.Abs(zoom - targetZoom) < 0.0001 && Math.Abs(x) < 0.01 && Math.Abs(y) < 0.01)
            Pending = false;
        return !Pending;
    }
}
