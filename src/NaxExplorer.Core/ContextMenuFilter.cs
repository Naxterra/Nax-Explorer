namespace NaxExplorer.Core;

/// <summary>
/// Pure filtering and placement rules for Windows shell context-menu entries merged into Nax Explorer's own menu.
/// Removes duplicate verbs that Nax Explorer already provides, hides "Create shortcut" ("link"), collapses stray
/// separators, and promotes Windows' usable "Open with" entry into the app's Open With slot right after Open.
/// </summary>
public static class ContextMenuFilter
{
    /// <summary>
    /// Commands the app's own menu already provides, commands that open Explorer windows, and "Create shortcut"
    /// ("link"), which is intentionally omitted.
    /// </summary>
    private static readonly HashSet<string> ExcludedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "explore", "opennewwindow", "opennewprocess", "opennewtab", "cut", "copy", "paste", "delete",
        "rename", "properties", "copyaspath", "pintohome", "find", "refresh", "undo", "redo", "link",
        ExplorerReplacement.Verb,
    };

    public static bool IsExcludedVerb(string? verb, bool hideWindowsStartPin, bool hideWindowsExtract)
    {
        if (string.IsNullOrEmpty(verb)) return false;
        if (ExcludedVerbs.Contains(verb)) return true;
        if (hideWindowsStartPin && string.Equals(verb, "pintostartscreen", StringComparison.OrdinalIgnoreCase)) return true;
        if (hideWindowsExtract && (string.Equals(verb, "extract", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "extractall", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>
    /// Filters a tree of shell menu entries, optionally pulling out Windows' usable "Open with" entry (a single
    /// enabled "openas" command or a submenu containing one) so the caller can place it directly after "Open".
    /// </summary>
    public static (T? PromotedOpenWith, List<T> Remaining) FilterAndExtractOpenWith<T>(
        IEnumerable<T> entries,
        Func<T, bool> isSeparator,
        Func<T, string?> getVerb,
        Func<T, string> getText,
        Func<T, bool> isEnabled,
        Func<T, IReadOnlyList<T>> getChildren,
        Func<T, IReadOnlyList<T>, T> withChildren,
        bool promoteOpenWith,
        bool hideWindowsStartPin,
        bool hideWindowsExtract) where T : class
    {
        T? promoted = null;
        List<T> filtered = FilterLevel(
            entries,
            isTopLevel: true,
            isSeparator,
            getVerb,
            getText,
            isEnabled,
            getChildren,
            withChildren,
            promoteOpenWith,
            hideWindowsStartPin,
            hideWindowsExtract,
            ref promoted);
        return (promoted, filtered);
    }

    private static List<T> FilterLevel<T>(
        IEnumerable<T> entries,
        bool isTopLevel,
        Func<T, bool> isSeparator,
        Func<T, string?> getVerb,
        Func<T, string> getText,
        Func<T, bool> isEnabled,
        Func<T, IReadOnlyList<T>> getChildren,
        Func<T, IReadOnlyList<T>, T> withChildren,
        bool promoteOpenWith,
        bool hideWindowsStartPin,
        bool hideWindowsExtract,
        ref T? promoted) where T : class
    {
        var result = new List<T>();
        foreach (T entry in entries)
        {
            if (isSeparator(entry))
            {
                if (result.Count > 0 && !isSeparator(result[^1])) result.Add(entry);
                continue;
            }
            if (IsExcludedVerb(getVerb(entry), hideWindowsStartPin, hideWindowsExtract)) continue;
            if (getText(entry).Length == 0) continue;

            IReadOnlyList<T> rawChildren = getChildren(entry);
            T candidate = entry;
            if (rawChildren.Count > 0)
            {
                T? ignored = null;
                List<T> keptChildren = FilterLevel(
                    rawChildren,
                    isTopLevel: false,
                    isSeparator,
                    getVerb,
                    getText,
                    isEnabled,
                    getChildren,
                    withChildren,
                    promoteOpenWith: false,
                    hideWindowsStartPin,
                    hideWindowsExtract,
                    ref ignored);
                if (keptChildren.Count == 0) continue;
                candidate = withChildren(entry, keptChildren);
            }

            if (isTopLevel && promoteOpenWith && ContainsOpenAs(candidate, isSeparator, getVerb, getChildren))
            {
                if (promoted is null && ContainsEnabledOpenAs(candidate, isSeparator, getVerb, isEnabled, getChildren))
                {
                    promoted = candidate;
                }
                // Drop disabled or duplicate Open With entries from the lower shell section so the app's top Open With
                // slot is never duplicated by a greyed-out Windows Open With entry.
                continue;
            }

            result.Add(candidate);
        }
        while (result.Count > 0 && isSeparator(result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static bool ContainsOpenAs<T>(
        T entry,
        Func<T, bool> isSeparator,
        Func<T, string?> getVerb,
        Func<T, IReadOnlyList<T>> getChildren)
    {
        if (isSeparator(entry)) return false;
        IReadOnlyList<T> children = getChildren(entry);
        if (children.Count == 0)
            return string.Equals(getVerb(entry), "openas", StringComparison.OrdinalIgnoreCase);
        foreach (T child in children)
        {
            if (ContainsOpenAs(child, isSeparator, getVerb, getChildren)) return true;
        }
        return false;
    }

    private static bool ContainsEnabledOpenAs<T>(
        T entry,
        Func<T, bool> isSeparator,
        Func<T, string?> getVerb,
        Func<T, bool> isEnabled,
        Func<T, IReadOnlyList<T>> getChildren)
    {
        if (isSeparator(entry)) return false;
        IReadOnlyList<T> children = getChildren(entry);
        if (children.Count == 0)
            return isEnabled(entry) && string.Equals(getVerb(entry), "openas", StringComparison.OrdinalIgnoreCase);
        if (!isEnabled(entry)) return false;
        foreach (T child in children)
        {
            if (ContainsEnabledOpenAs(child, isSeparator, getVerb, isEnabled, getChildren)) return true;
        }
        return false;
    }
}
