using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NaxExplorer.Core;

/// <summary>Registers a private shortcut without invoking Windows' unrelated Pin to Start shell verb.</summary>
public static class StartMenuPinning
{
    public static bool CanPin(string path) => Path.IsPathFullyQualified(path) &&
        new[] { ".lnk", ".exe", ".url" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Call on an STA worker. A copied link retains arguments, working directory, icon and elevation flags.</summary>
    public static string Register(string source, string? programsFolder = null)
    {
        if (!CanPin(source) || !File.Exists(source)) throw new IOException("The application or shortcut no longer exists.");
        string programs = programsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrEmpty(programs)) throw new IOException("The Start Menu Programs folder is unavailable.");
        if (Path.GetFullPath(source).StartsWith(Path.GetFullPath(programs).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(source);
        // One stable directory per source avoids overwriting another app with the same display name.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..16];
        string folder = Path.Combine(programs, "Nax pinned apps", key);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) +
            (Path.GetExtension(source).Equals(".url", StringComparison.OrdinalIgnoreCase) ? ".url" : ".lnk"));
        if (Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object? shortcut = null;
            try
            {
                shortcut = ((dynamic)shell).CreateShortcut(target);
                ((dynamic)shortcut).TargetPath = source;
                ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(source)!;
                ((dynamic)shortcut).Save();
            }
            finally
            {
                if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }
        else File.Copy(source, target, overwrite: true);
        return target;
    }
}
