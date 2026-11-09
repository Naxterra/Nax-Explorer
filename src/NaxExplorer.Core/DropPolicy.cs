namespace NaxExplorer.Core;

public static class DropPolicy
{
    /// <summary>Explicit modifiers win; unmodified drags move only when every source shares the target drive.</summary>
    public static bool Moves(bool control, bool shift, IReadOnlyList<string>? sources, string target)
    {
        if (control) return false;
        if (shift) return true;
        string? root = Path.GetPathRoot(target);
        return sources is { Count: > 0 } && !string.IsNullOrEmpty(root)
            && sources.All(path => string.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase));
    }
}
