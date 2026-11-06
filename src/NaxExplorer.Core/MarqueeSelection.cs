namespace NaxExplorer.Core;

/// <summary>Changes only the rows entering or leaving a mouse selection box.</summary>
public sealed class MarqueeSelection<T>(IEnumerable<T> retained) where T : notnull
{
    private readonly HashSet<T> _retained = retained.ToHashSet();
    private HashSet<T> _inside = [];

    public (IReadOnlyList<T> Added, IReadOnlyList<T> Removed) Update(IEnumerable<T> intersecting)
    {
        HashSet<T> next = intersecting.ToHashSet();
        T[] added = next.Where(item => !_inside.Contains(item) && !_retained.Contains(item)).ToArray();
        T[] removed = _inside.Where(item => !next.Contains(item) && !_retained.Contains(item)).ToArray();
        _inside = next;
        return (added, removed);
    }
}
