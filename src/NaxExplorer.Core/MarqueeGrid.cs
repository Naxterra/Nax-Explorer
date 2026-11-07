namespace NaxExplorer.Core;

/// <summary>Hit tests a uniform virtualized layout in content coordinates, including unrealized rows.</summary>
public readonly record struct MarqueeGrid(double X, double Y, double Width, double Height, double PitchX, double PitchY, int Columns)
{
    public IEnumerable<(int Start, uint Length)> Intersect(double left, double top, double right, double bottom, int count)
    {
        if (count <= 0 || Columns <= 0 || PitchY <= 0 || PitchX <= 0 || right <= left || bottom <= top) yield break;
        int firstRow = (int)Math.Max(0, Math.Floor((top - Y - Height) / PitchY) + 1);
        int lastRow = (int)Math.Min((count - 1) / Columns, Math.Ceiling((bottom - Y) / PitchY) - 1);
        int firstColumn = (int)Math.Max(0, Math.Floor((left - X - Width) / PitchX) + 1);
        int lastColumn = (int)Math.Min(Columns - 1, Math.Ceiling((right - X) / PitchX) - 1);
        if (firstColumn > lastColumn) yield break;
        // Full-width rows form one native range, however large the folder is.
        if (firstColumn == 0 && lastColumn == Columns - 1 && firstRow <= lastRow)
        {
            int start = firstRow * Columns;
            yield return (start, (uint)(Math.Min((long)count, ((long)lastRow + 1) * Columns) - start));
            yield break;
        }
        for (int row = firstRow; row <= lastRow; row++)
        {
            long start = (long)row * Columns + firstColumn;
            long end = Math.Min(count, (long)row * Columns + lastColumn + 1);
            if (start < end) yield return ((int)start, (uint)(end - start));
        }
    }
}
