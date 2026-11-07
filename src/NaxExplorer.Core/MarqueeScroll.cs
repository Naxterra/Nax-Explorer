namespace NaxExplorer.Core;

public static class MarqueeScroll
{
    public static double Speed(double position, double viewport)
    {
        if (viewport <= 0) return 0;
        double edge = Math.Min(48, viewport / 3);
        if (position < edge) return -900 * Math.Clamp((edge - position) / edge, 0, 1);
        if (position > viewport - edge) return 900 * Math.Clamp((position - viewport + edge) / edge, 0, 1);
        return 0;
    }
}
