namespace NaxExplorer.Core;

/// <summary>Combines changed item indices into contiguous native selection operations.</summary>
public static class SelectionRuns
{
    public static IEnumerable<(int Start, uint Length)> FromIndices(IEnumerable<int> indices)
    {
        int start = -1, last = -1;
        foreach (int index in indices.Where(i => i >= 0).Distinct().Order())
        {
            if (start < 0) start = index;
            else if ((long)index != (long)last + 1)
            {
                yield return (start, (uint)((long)last - start + 1));
                start = index;
            }
            last = index;
        }
        if (start >= 0) yield return (start, (uint)((long)last - start + 1));
    }
}
