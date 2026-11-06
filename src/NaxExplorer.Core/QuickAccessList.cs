namespace NaxExplorer.Core;

/// <summary>
/// The entries of the sidebar's quick access, which the user can add to, remove from, reorder and reset. An entry is
/// either one of Windows' own folders, named by a key such as <see cref="Downloads"/>, or the path of a folder.
/// </summary>
public static class QuickAccessList
{
    public const string Home = AppSettings.StartHome;
    public const string Desktop = "::Desktop";
    public const string Downloads = "::Downloads";
    public const string Documents = "::Documents";
    public const string Pictures = "::Pictures";
    public const string Music = "::Music";
    public const string Videos = "::Videos";
    public const string RecycleBin = "::RecycleBin";

    /// <summary>The list as it comes with the app, and as "reset" brings it back.</summary>
    public static IReadOnlyList<string> Defaults { get; } = [Home, Desktop, Downloads, Documents, Pictures, Music, Videos, RecycleBin];

    /// <summary>True for an entry that names one of Windows' own folders rather than a path.</summary>
    public static bool IsKey(string entry) => entry.StartsWith("::", StringComparison.Ordinal);

    /// <summary>
    /// The current entries. Until the user changes the list, these are the defaults followed by the folders pinned
    /// with an earlier version, which had them in a separate list.
    /// </summary>
    public static List<string> Current(AppSettings settings)
    {
        if (settings.QuickAccess is { } chosen) return [.. chosen];
        var list = Defaults.ToList();
        foreach (string pinned in settings.Pinned)
        {
            if (!list.Any(e => Same(e, pinned))) list.Add(pinned);
        }
        return list;
    }

    /// <summary>Adds a folder at the end; a folder already in the list is not added twice.</summary>
    public static void Add(AppSettings settings, string folder)
    {
        List<string> list = Current(settings);
        if (!list.Any(e => Same(e, folder))) list.Add(folder);
        Keep(settings, list);
    }

    public static void Remove(AppSettings settings, string entry)
    {
        List<string> list = Current(settings);
        list.RemoveAll(e => Same(e, entry));
        Keep(settings, list);
    }

    /// <summary>Moves an entry up (negative steps) or down (positive steps), staying inside the list.</summary>
    public static void Move(AppSettings settings, string entry, int steps)
    {
        List<string> list = Current(settings);
        int from = list.FindIndex(e => Same(e, entry));
        if (from < 0) return;
        int to = Math.Clamp(from + steps, 0, list.Count - 1);
        if (to == from) return;
        string moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);
        Keep(settings, list);
    }

    /// <summary>Brings back the list as it comes with the app; folders the user added are removed.</summary>
    public static void Reset(AppSettings settings)
    {
        settings.QuickAccess = null;
        settings.Pinned = [];
    }

    private static void Keep(AppSettings settings, List<string> list)
    {
        settings.QuickAccess = list;
        // The pinned folders of an earlier version are part of the list now.
        settings.Pinned = [];
    }

    private static bool Same(string a, string b) =>
        IsKey(a) || IsKey(b) ? string.Equals(a, b, StringComparison.Ordinal) : PathTools.SameLocation(a, b);
}
