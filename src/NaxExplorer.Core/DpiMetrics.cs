namespace NaxExplorer.Core;

/// <summary>Converts physical image pixels and scroll positions when a window's rasterization scale changes.</summary>
public static class DpiMetrics
{
    public static double LogicalPixels(double pixels, double scale) => pixels / ValidScale(scale);

    public static double RescaleOffset(double offset, double oldScale, double newScale) =>
        Math.Max(0, offset) * ValidScale(oldScale) / ValidScale(newScale);

    private static double ValidScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
}
