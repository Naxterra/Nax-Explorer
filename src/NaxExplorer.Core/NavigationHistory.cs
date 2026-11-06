namespace NaxExplorer.Core;

/// <summary>The back and forward history of one tab.</summary>
public sealed class NavigationHistory
{
    private const int Limit = 100;
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    public string? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public IReadOnlyList<string> BackEntries => _back;

    /// <summary>Records a visit. Visiting the place that is already current changes nothing.</summary>
    public void Visit(string location)
    {
        if (Current is not null && PathTools.SameLocation(Current, location)) return;
        if (Current is not null)
        {
            _back.Add(Current);
            if (_back.Count > Limit) _back.RemoveAt(0);
        }
        _forward.Clear();
        Current = location;
    }

    public string? GoBack()
    {
        if (_back.Count == 0) return null;
        if (Current is not null) _forward.Add(Current);
        Current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return Current;
    }

    public string? GoForward()
    {
        if (_forward.Count == 0) return null;
        if (Current is not null) _back.Add(Current);
        Current = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        return Current;
    }
}
