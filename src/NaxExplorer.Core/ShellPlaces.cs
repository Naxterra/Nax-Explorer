namespace NaxExplorer.Core;

/// <summary>What the app does with a place Windows hands it while it opens folders instead of Explorer.</summary>
public enum ShellPlace
{
    /// <summary>Not a special place: a folder path, or nothing.</summary>
    None,

    /// <summary>Home, which Win+E opens: the app starts with its own start folders.</summary>
    Start,

    /// <summary>This PC, which the app shows itself.</summary>
    ThisPc,

    /// <summary>
    /// A place that is not a folder on disk, such as the Recycle Bin, Control Panel, Network or a library. The app lists
    /// most of them itself and hands only those it leaves to Explorer, such as Control Panel, on to Explorer.
    /// </summary>
    Explorer,
}

/// <summary>
/// Sorts the special places Windows passes as "::{CLSID}" when the app opens folders instead of Explorer (see
/// <see cref="ExplorerReplacement"/>). Ordinary folders arrive as paths and are not special places.
/// </summary>
public static class ShellPlaces
{
    public const string HomeClsid = "{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
    public const string ThisPcClsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    public static ShellPlace Classify(string argument)
    {
        string place = argument.Trim().Trim('"').Trim();
        if (place.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            place = place[6..];
            // A named shell folder such as shell:RecycleBinFolder is for Explorer to resolve.
            if (!place.StartsWith("::", StringComparison.Ordinal)) return ShellPlace.Explorer;
        }
        if (!place.StartsWith("::", StringComparison.Ordinal)) return ShellPlace.None;
        string clsid = place[2..].TrimEnd('\\');
        if (Same(clsid, HomeClsid) || Same(clsid, ExplorerReplacement.WinEClsid)) return ShellPlace.Start;
        if (Same(clsid, ThisPcClsid)) return ShellPlace.ThisPc;
        // Everything else, also places below This PC such as a phone, stays with Explorer.
        return ShellPlace.Explorer;
    }

    /// <summary>
    /// What to do with one command-line argument. A special place is sorted as above. Of everything else, only what
    /// Windows asked to open as a folder but only Explorer can show goes to Explorer: a computer under Network such as
    /// \\SERVER or \\wsl.localhost, and a folder inside a zip file. Everything else stays with the app: existing
    /// folders and files, the app's own switches, empty arguments, and folders that cannot be reached right now, such
    /// as an offline network drive, for which the app opens its start folders as before. The bare computer name is
    /// recognized by its shape alone. Any other path is asked for once with <paramref name="exists"/>. For a path that
    /// does not exist, its drive or share is asked next, and only when that answers are its parents with an extension,
    /// as an archive's name has, asked whether they are a file. So an offline network folder costs at most two waits
    /// for the network however deep it lies, and a zip file anywhere up the path of a reachable drive is still found.
    /// </summary>
    public static ShellPlace Route(string argument, Func<string, bool> exists, Func<string, bool> isFile)
    {
        ShellPlace place = Classify(argument);
        if (place != ShellPlace.None) return place;
        string value = argument.Trim().Trim('"').Trim();
        if (value.Length == 0 || value.StartsWith('-') || value.StartsWith('/')) return ShellPlace.None;
        if (value.EndsWith("Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase)) return ShellPlace.None;
        if (IsBareComputer(value)) return ShellPlace.Explorer;
        string full = PathTools.Normalize(value, null) ?? value;
        if (exists(full)) return ShellPlace.None;
        return InsideFile(full, exists, isFile) ? ShellPlace.Explorer : ShellPlace.None;
    }

    /// <summary>True for \\NAME without a share, which names a computer rather than a folder.</summary>
    private static bool IsBareComputer(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal)
        && !value.StartsWith(@"\\?\", StringComparison.Ordinal) && !value.StartsWith(@"\\.\", StringComparison.Ordinal)
        && value.Length > 2 && value.IndexOf('\\', 2) is var next && (next < 0 || next == value.Length - 1);

    /// <summary>
    /// True when one of the path's parents is a file, as for C:\Archiv.zip\Ordner inside a zip file. Only parents whose
    /// name has an extension can be one, so a path without such a parent asks nothing more. Otherwise the drive or share
    /// is asked first, because every question about an unreachable network waits for its timeout; when it does not
    /// answer, no parent is asked.
    /// </summary>
    private static bool InsideFile(string path, Func<string, bool> exists, Func<string, bool> isFile)
    {
        var candidates = new List<string>();
        for (string? parent = Path.GetDirectoryName(path.TrimEnd('\\')); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (Path.HasExtension(parent)) candidates.Add(parent);
        }
        if (candidates.Count == 0) return false;
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !exists(root)) return false;
        return candidates.Any(isFile);
    }

    /// <summary>How long the hand-offs of one place are counted, and how many of them make a loop.</summary>
    public static readonly TimeSpan HandOffWindow = TimeSpan.FromSeconds(30);
    public const int HandOffLimit = 4;

    /// <summary>
    /// A guard against a loop between the app and Explorer that the marker on Explorer's start cannot see, for example
    /// when Explorer passes the place to its running window process: true when the place may be handed to Explorer,
    /// false when the same place was already handed over <see cref="HandOffLimit"/> times within
    /// <see cref="HandOffWindow"/>. Opening the same place again a few times, as people do, stays allowed; a loop,
    /// even a slow one, stops after a few rounds. The hand-offs are kept in <paramref name="stateFile"/>, because every
    /// hand-off is a new start of the app.
    /// </summary>
    public static bool MayHandOff(string place, string stateFile, DateTime nowUtc)
    {
        place = PlaceKey(place);
        try
        {
            var recent = new List<(string Place, long Ticks)>();
            if (File.Exists(stateFile))
            {
                foreach (string line in File.ReadAllLines(stateFile))
                {
                    int bar = line.LastIndexOf('|');
                    if (bar > 0 && long.TryParse(line[(bar + 1)..], out long ticks) && nowUtc - new DateTime(ticks, DateTimeKind.Utc) < HandOffWindow)
                    {
                        recent.Add((line[..bar], ticks));
                    }
                }
            }
            if (recent.Count(r => string.Equals(r.Place, place, StringComparison.OrdinalIgnoreCase)) >= HandOffLimit) return false;
            recent.Add((place, nowUtc.Ticks));
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
            File.WriteAllLines(stateFile, recent.TakeLast(50).Select(r => $"{r.Place}|{r.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the note the hand-off still happens; the guard only stops a repeat.
        }
        return true;
    }

    /// <summary>
    /// Set on the Explorer the app starts for a place. Should that Explorer start the app again for the same place,
    /// the app sees the marker in its inherited environment and knows at once that it is a loop. The marker carries the
    /// time of the hand-off and counts only within <see cref="HandOffWindow"/>, because an Explorer window process can
    /// live on and pass the marker to programs started from it much later.
    /// </summary>
    public const string HandOffMarker = "NAXEXPLORER_HANDOFF";

    /// <summary>The value of <see cref="HandOffMarker"/> for a hand-off of <paramref name="place"/> now.</summary>
    public static string MarkerValue(string place, DateTime nowUtc) =>
        $"{nowUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{PlaceKey(place)}";

    /// <summary>True when the inherited marker says this start comes from the app's own hand-off of the same place a moment ago.</summary>
    public static bool IsOwnHandOff(string? marker, string place, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(marker)) return false;
        int bar = marker.IndexOf('|');
        if (bar <= 0 || !long.TryParse(marker[..bar], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks)) return false;
        if (ticks > DateTime.MaxValue.Ticks) return false;
        TimeSpan age = nowUtc - new DateTime(ticks, DateTimeKind.Utc);
        return age >= TimeSpan.Zero && age < HandOffWindow && string.Equals(marker[(bar + 1)..], PlaceKey(place), StringComparison.Ordinal);
    }

    /// <summary>
    /// One spelling for a place, so a loop is seen even when Explorer passes the place back in another form: without
    /// quotes or surrounding spaces, without trailing backslashes (a drive root keeps its own), and in upper case.
    /// </summary>
    public static string PlaceKey(string place)
    {
        string value = place.Trim().Trim('"').Trim();
        while (value.Length > 1 && value.EndsWith('\\') && !(value.Length == 3 && value[1] == ':')) value = value[..^1];
        return value.ToUpperInvariant();
    }

    /// <summary>
    /// Control Panel and its pages, which are Windows' own settings pages rather than lists of items, so the app leaves
    /// them to Explorer. Every other place Windows lists, such as the Recycle Bin or Network, the app shows itself.
    /// </summary>
    private static readonly string[] ExplorerOnly =
    [
        "{26EE0668-A00A-44D7-9371-BEB064C98683}", // Control Panel
        "{21EC2020-3AEA-1069-A2DD-08002B30309D}", // All Control Panel items
        "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", // Control Panel home
        "{ED7BA470-8E54-465E-825C-99712043E01C}", // All tasks
        "ControlPanelFolder",
    ];

    /// <summary>True for a place the app leaves to Explorer even though Windows can list it (see <see cref="ExplorerOnly"/>).</summary>
    public static bool OnlyExplorerShows(string place) => ExplorerOnly.Any(id => place.Contains(id, StringComparison.OrdinalIgnoreCase));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
