namespace NaxExplorer.Core;

/// <summary>The name being edited, independent of row recycling and delayed focus callbacks.</summary>
public sealed class RenameEditState
{
    private int? _initialSelection;
    public bool IsActive { get; private set; }
    public string Text { get; private set; } = "";

    public void Begin(string name, bool isFile)
    {
        Text = name;
        int dot = isFile ? name.LastIndexOf('.') : -1;
        _initialSelection = dot > 0 ? dot : name.Length;
        IsActive = true;
    }

    public void UpdateText(string text)
    {
        if (IsActive) Text = text;
    }

    public bool TakeInitialSelection(out int length)
    {
        length = _initialSelection ?? 0;
        if (!IsActive || _initialSelection is null) return false;
        _initialSelection = null;
        return true;
    }

    public void End()
    {
        IsActive = false;
        _initialSelection = null;
    }
}
