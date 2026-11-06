namespace NaxExplorer.Core;

/// <summary>Keeps a previous image visible while a new size loads, without accepting an obsolete completion.</summary>
public sealed class ImageRequestState
{
    private string? _requested;
    private string? _delivered;

    public bool Begin(string key, bool hasImage)
    {
        _requested = key;
        return !hasImage || _delivered != key;
    }

    public bool Complete(string key)
    {
        if (_requested != key) return false;
        _delivered = key;
        return true;
    }
}
