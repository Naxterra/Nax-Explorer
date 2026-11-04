using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaxExplorer.Core;

/// <summary>Where the app keeps its own files. Nothing is written anywhere else.</summary>
public static class DataFolders
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nax-Explorer");

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Models => Path.Combine(Root, "models");

    public static string Index => Path.Combine(Root, "index");

    public static string CrashLog => Path.Combine(Root, "crash.log");

    /// <summary>The last place handed to Explorer, so a loop between the app and Explorer stops after one round.</summary>
    public static string HandOff => Path.Combine(Root, "handoff.txt");
}

public sealed class PaneSettings
{
    public List<string> Tabs { get; set; } = [];

    public int ActiveTab { get; set; }

    /// <summary>"details" or "thumbnails".</summary>
    public string View { get; set; } = "details";

    public SortColumn Sort { get; set; } = SortColumn.Name;

    public bool Descending { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>The user's settings, kept in settings.json in the app's data folder.</summary>
public sealed class AppSettings
{
    /// <summary>"en-US", "de-DE", or empty to follow the Windows display language.</summary>
    public string Language { get; set; } = "";

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    public bool DualPane { get; set; } = true;

    public bool ShowPreview { get; set; } = true;

    public bool ShowHidden { get; set; }

    /// <summary>Shows items that are both hidden and system, such as desktop.ini, as Explorer's "protected operating system files".</summary>
    public bool ShowProtectedFiles { get; set; }

    public bool ShowExtensions { get; set; } = true;

    /// <summary>What the left pane opens at start: empty for the folders of the last session, or a place (see <see cref="StartPlace"/>).</summary>
    public string LeftStart { get; set; } = "";

    /// <summary>What the right pane opens at start, like <see cref="LeftStart"/>.</summary>
    public string RightStart { get; set; } = "";

    /// <summary>
    /// Folders, drives and Win+E open Nax Explorer instead of Explorer. Off until the user turns it on, because it
    /// changes entries for this user in the registry (see <see cref="ExplorerReplacement"/>).
    /// </summary>
    public bool ReplaceExplorer { get; set; }

    public bool ColorfulNames { get; set; } = true;

    public bool OpenImagesInViewer { get; set; } = true;

    public double SidebarWidth { get; set; } = 240;

    public double PreviewWidth { get; set; } = 340;

    public PaneSettings Left { get; set; } = new();

    public PaneSettings Right { get; set; } = new();

    /// <summary>Folders pinned with an earlier version; they now belong to <see cref="QuickAccess"/>.</summary>
    public List<string> Pinned { get; set; } = [];

    /// <summary>The quick access entries in their order, or null for the defaults (see <see cref="QuickAccessList"/>).</summary>
    public List<string>? QuickAccess { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>True once the user turned on AI search; the model is downloaded at that moment.</summary>
    public bool AiSearch { get; set; }

    /// <summary>The folders the AI index covers. Empty until AI search is turned on, then Documents, Desktop and Downloads.</summary>
    public List<string> IndexedFolders { get; set; } = [];

    public bool UseGpu { get; set; } = true;

    public bool PauseIndexingInFullScreen { get; set; } = true;

    public bool PauseIndexingOnBattery { get; set; } = true;

    /// <summary>
    /// Reads the settings. A missing or damaged file gives the defaults, so a bad file never stops the app. Without a
    /// usable file, Explorer's choices for hidden items, protected system files and file name extensions are taken
    /// over, so the same folder shows the same items in both; a file that was read keeps the app's own choices.
    /// </summary>
    public static AppSettings Load(string? path = null, ExplorerView? explorer = null)
    {
        path ??= DataFolders.Settings;
        try
        {
            if (JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings) is { } settings)
            {
                settings.Left ??= new PaneSettings();
                settings.Right ??= new PaneSettings();
                settings.Pinned ??= [];
                settings.IndexedFolders ??= [];
                settings.LeftStart ??= "";
                settings.RightStart ??= "";
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
        ExplorerView view = explorer ?? ExplorerView.Read();
        return new AppSettings
        {
            ShowHidden = view.ShowHidden,
            ShowProtectedFiles = view.ShowProtectedFiles,
            ShowExtensions = view.ShowExtensions,
        };
    }

    /// <summary>The value of a start setting that opens the user's home folder.</summary>
    public const string StartHome = "::Home";

    /// <summary>
    /// The place a pane opens at start, or null for the folders of the last session. A start folder that no longer
    /// exists falls back to the last session as well.
    /// </summary>
    public static string? StartPlace(string setting, string home)
    {
        if (string.IsNullOrEmpty(setting)) return null;
        if (setting == StartHome) return home;
        if (PathTools.IsThisPc(setting)) return PathTools.ThisPc;
        return Directory.Exists(setting) ? setting : null;
    }

    /// <summary>Writes the settings through a temporary file, so a crash never leaves a half-written file behind.</summary>
    public void Save(string? path = null)
    {
        path ??= DataFolders.Settings;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        File.Move(temporary, path, overwrite: true);
    }

    public static List<string> DefaultIndexedFolders()
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) folders.Add(downloads);
        return folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Explorer's view choices, from the values Explorer keeps in the registry.</summary>
public sealed record ExplorerView(bool ShowHidden, bool ShowProtectedFiles, bool ShowExtensions)
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>Explorer's values: Hidden is 1 to show hidden items, ShowSuperHidden is 1 to show protected system files,
    /// HideFileExt is 0 to show extensions. Missing values mean Explorer's defaults.</summary>
    public static ExplorerView From(int? hidden, int? superHidden, int? hideExtensions) =>
        new(hidden == 1, superHidden == 1, hideExtensions == 0);

    public static ExplorerView Read()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return From(key?.GetValue("Hidden") as int?, key?.GetValue("ShowSuperHidden") as int?, key?.GetValue("HideFileExt") as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return From(null, null, null);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
