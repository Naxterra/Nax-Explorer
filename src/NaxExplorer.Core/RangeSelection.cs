namespace NaxExplorer.Core;

/// <summary>Updates a selection without enumerating every selected item.</summary>
public sealed class RangeSelection(IEnumerable<(int Start, uint Length)> retained)
{
    private readonly (int Start, uint Length)[] _retained = Normalize(retained);
    private (int Start, uint Length)[] _current = Normalize(retained);

    public ((int Start, uint Length)[] Added, (int Start, uint Length)[] Removed) Update(IEnumerable<(int Start, uint Length)> inside)
    {
        var next = Normalize(_retained.Concat(inside));
        var result = (Except(next, _current), Except(_current, next));
        _current = next;
        return result;
    }

    private static (int Start, uint Length)[] Normalize(IEnumerable<(int Start, uint Length)> ranges)
    {
        var result = new List<(int Start, uint Length)>();
        foreach (var range in ranges.Where(r => r.Start >= 0 && r.Length > 0).OrderBy(r => r.Start))
        {
            long end = Math.Min((long)int.MaxValue + 1, (long)range.Start + range.Length);
            if (result.Count > 0 && range.Start <= (long)result[^1].Start + result[^1].Length)
            {
                var previous = result[^1];
                result[^1] = (previous.Start, (uint)(Math.Max(end, (long)previous.Start + previous.Length) - previous.Start));
            }
            else result.Add((range.Start, (uint)(end - range.Start)));
        }
        return result.ToArray();
    }

    private static (int Start, uint Length)[] Except((int Start, uint Length)[] source, (int Start, uint Length)[] excluded)
    {
        var result = new List<(int Start, uint Length)>();
        int j = 0;
        foreach (var range in source)
        {
            long start = range.Start, end = start + range.Length;
            while (j < excluded.Length && (long)excluded[j].Start + excluded[j].Length <= start) j++;
            for (int k = j; k < excluded.Length && excluded[k].Start < end; k++)
            {
                if (excluded[k].Start > start) result.Add(((int)start, (uint)(excluded[k].Start - start)));
                start = Math.Max(start, (long)excluded[k].Start + excluded[k].Length);
                if (start >= end) break;
            }
            if (start < end) result.Add(((int)start, (uint)(end - start)));
        }
        return result.ToArray();
    }
}
