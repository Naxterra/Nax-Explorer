using System.Globalization;

namespace NaxExplorer.Core;

/// <summary>Writes byte counts the way the file list shows them, in binary units with at most one decimal.</summary>
public static class SizeFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024) return bytes.ToString("N0", culture) + " B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string number = value >= 100 ? value.ToString("N0", culture) : value.ToString("0.#", culture);
        return number + " " + Units[unit];
    }
}
