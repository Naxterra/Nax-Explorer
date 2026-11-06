namespace NaxExplorer.Core;

/// <summary>Short-lived preview totals. A folder change also retires counts which are still being computed.</summary>
public sealed class FolderTotalsCache
{
    private readonly Dictionary<string, (FolderTotals Totals, DateTime When)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public long Generation { get; private set; }

    public bool TryGet(string key, out FolderTotals? totals)
    {
        if (_entries.TryGetValue(key, out var known) && DateTime.UtcNow - known.When < TimeSpan.FromMinutes(2))
        {
            totals = known.Totals;
            return true;
        }
        totals = null;
        return false;
    }

    public void Remember(string key, FolderTotals totals, long generation)
    {
        if (generation != Generation) return;
        if (_entries.Count > 200) _entries.Clear();
        _entries[key] = (totals, DateTime.UtcNow);
    }

    public void Invalidate()
    {
        Generation++;
        _entries.Clear();
    }
}
