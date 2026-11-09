namespace NaxExplorer.Core;

/// <summary>Keeps the newest pointer position until the next rendering opportunity.</summary>
public sealed class FrameInput<T>
{
    private T _latest = default!;
    public bool Pending { get; private set; }
    public void Submit(T value) { _latest = value; Pending = true; }
    public bool TryTake(out T value)
    {
        value = _latest;
        bool pending = Pending;
        Clear();
        return pending;
    }
    public void Clear() { _latest = default!; Pending = false; }
}
