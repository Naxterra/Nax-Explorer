namespace NaxExplorer.Core;

/// <summary>The broad kind of a file, which decides its colour in the list and how the preview shows it.</summary>
public enum FileCategory
{
    Folder,
    Drive,
    Image,
    Video,
    Audio,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Archive,
    Code,
    Text,
    Program,
    Font,
    DiskImage,
    Shortcut,
    Other,
}

public static class FileTypes
{
    /// <summary>Image formats the built-in viewer opens through the Windows imaging codecs.</summary>
    private static readonly HashSet<string> ViewerImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp",
        ".heic", ".heif", ".avif", ".jxr", ".wdp", ".jxl",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".ini", ".cfg",
        ".conf", ".config", ".toml", ".properties", ".env", ".gitignore", ".gitattributes", ".editorconfig",
        ".sql", ".ora", ".trc", ".lst", ".pls", ".pks", ".pkb", ".plsql", ".ctl", ".par", ".rsp",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish", ".vbs", ".ahk",
        ".cs", ".csx", ".vb", ".fs", ".xaml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".slnx", ".sln", ".resx", ".resw",
        ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".inl", ".m", ".mm", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less",
        ".py", ".pyw", ".rb", ".php", ".pl", ".pm", ".r", ".lua", ".go", ".rs", ".swift", ".dart", ".zig", ".nim", ".jl",
        ".tex", ".bib", ".rst", ".adoc", ".org", ".srt", ".vtt", ".sub", ".nfo", ".diz", ".reg", ".inf", ".iss", ".nsi",
        ".dockerfile", ".tf", ".hcl", ".graphql", ".proto", ".cmake", ".mk", ".makefile", ".asm", ".s", ".eml", ".ics", ".vcf",
    };

    // Built from the sets above, so it must be declared after them.
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    public static FileCategory Categorize(FileEntry entry)
    {
        if (entry.Kind == EntryKind.Drive) return FileCategory.Drive;
        if (entry.Kind == EntryKind.Folder) return FileCategory.Folder;
        return Categorize(entry.Extension);
    }

    public static FileCategory Categorize(string extension) =>
        ByExtension.TryGetValue(extension, out FileCategory category) ? category : FileCategory.Other;

    public static bool IsViewerImage(string extension) => ViewerImages.Contains(extension);

    /// <summary>True for extensions that are known to hold plain text, such as source code and logs.</summary>
    public static bool IsKnownText(string extension) => TextExtensions.Contains(extension);

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, params string[] extensions)
        {
            foreach (string extension in extensions) map[extension] = category;
        }

        Add(FileCategory.Image, ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".webp",
            ".heic", ".heif", ".avif", ".jxr", ".wdp", ".jxl", ".svg", ".psd", ".xcf", ".raw", ".dng", ".cr2", ".cr3", ".nef",
            ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef", ".eps", ".ai", ".emf", ".wmf", ".tga", ".dds", ".exr", ".hdr");
        Add(FileCategory.Video, ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts3", ".3gp", ".ogv", ".vob");
        Add(FileCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".mid", ".midi", ".ape", ".alac");
        Add(FileCategory.Document, ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf", ".pages", ".wpd", ".xps", ".oxps", ".epub", ".mobi", ".one", ".msg");
        Add(FileCategory.Spreadsheet, ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ods", ".numbers");
        Add(FileCategory.Presentation, ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp", ".key");
        Add(FileCategory.Pdf, ".pdf");
        Add(FileCategory.Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".lz", ".lzma", ".arj", ".z", ".wim", ".nupkg");
        Add(FileCategory.Program, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".com", ".scr", ".cpl", ".msc", ".dll", ".sys", ".jar", ".appref-ms");
        Add(FileCategory.Font, ".ttf", ".otf", ".ttc", ".woff", ".woff2", ".fon", ".fnt");
        Add(FileCategory.DiskImage, ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".dmg", ".qcow2", ".bin", ".cue");
        Add(FileCategory.Shortcut, ".lnk", ".url", ".website");
        foreach (string extension in TextExtensions)
        {
            if (map.ContainsKey(extension)) continue;
            bool plain = extension is ".txt" or ".md" or ".markdown" or ".log" or ".csv" or ".tsv" or ".nfo" or ".diz" or ".srt" or ".vtt"
                or ".sub" or ".rst" or ".adoc" or ".org" or ".eml" or ".ics" or ".vcf" or ".trc" or ".lst";
            map[extension] = plain ? FileCategory.Text : FileCategory.Code;
        }
        return map;
    }
}
