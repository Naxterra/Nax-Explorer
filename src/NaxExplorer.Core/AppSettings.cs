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

    public bool ShowExtensions { get; set; } = true;

    public bool ColorfulNames { get; set; } = true;

    public bool OpenImagesInViewer { get; set; } = true;

    public double SidebarWidth { get; set; } = 240;

    public double PreviewWidth { get; set; } = 340;

    public PaneSettings Left { get; set; } = new();

    public PaneSettings Right { get; set; } = new();

    public List<string> Pinned { get; set; } = [];

    public WindowPlacement? Window { get; set; }

    /// <summary>True once the user turned on AI search; the model is downloaded at that moment.</summary>
    public bool AiSearch { get; set; }

    /// <summary>The folders the AI index covers. Empty until AI search is turned on, then Documents, Desktop and Downloads.</summary>
    public List<string> IndexedFolders { get; set; } = [];

    public bool UseGpu { get; set; } = true;

    public bool PauseIndexingInFullScreen { get; set; } = true;

    public bool PauseIndexingOnBattery { get; set; } = true;

    /// <summary>Reads the settings. A missing or damaged file gives the defaults, so a bad file never stops the app.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DataFolders.Settings;
        try
        {
            AppSettings settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
            settings.Left ??= new PaneSettings();
            settings.Right ??= new PaneSettings();
            settings.Pinned ??= [];
            settings.IndexedFolders ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
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

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
