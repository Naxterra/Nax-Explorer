using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace NaxExplorer.Core;

/// <summary>A folder that a cloud sync app keeps in step with its online storage.</summary>
public sealed record CloudFolder(string Provider, string Name, string Path);

/// <summary>
/// Finds the folders of cloud sync apps on this PC. Apps that use Windows' cloud files platform, such as OneDrive,
/// Nextcloud, Dropbox and Proton Drive, register each folder as a "sync root"; Google Drive for desktop shows its files
/// as a mounted drive, a DriveFS configuration entry, an Explorer namespace folder, or a sync root. The app only reads
/// where these folders are; the sync apps keep doing the syncing.
/// </summary>
public static class CloudFolders
{
    private const string SyncRootManager = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    private const string DriveFsKey = @"Software\Google\DriveFS";
    private const string DriveFsShareKey = @"Software\Google\DriveFS\Share";
    private const string ExplorerNameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string UserClassesClsidKey = @"Software\Classes\CLSID";

    /// <summary>
    /// The cloud folders of the current user. With <paramref name="withDrives"/>, drives and Google Drive mount paths
    /// are probed for availability and volume metadata, which can be slow for a network drive, so callers on the UI
    /// thread leave it out.
    /// </summary>
    public static List<CloudFolder> Find(bool withDrives)
    {
        var found = new List<CloudFolder>();
        try
        {
            using RegistryKey? roots = Registry.LocalMachine.OpenSubKey(SyncRootManager);
            string? user = WindowsIdentity.GetCurrent().User?.Value;
            if (roots is not null && user is not null) found.AddRange(FromSyncRoots(roots, user));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        if (withDrives)
        {
            found.AddRange(FromGoogleDriveRegistry());
            found.AddRange(FromGoogleDriveNamespace());
            found.AddRange(GoogleDriveLetters());
        }
        return DeduplicateAndLabel(found);
    }

    /// <summary>True when the path is a cloud folder or lies inside one.</summary>
    public static bool Contains(IEnumerable<CloudFolder> folders, string path) => folders.Any(f => PathTools.IsSameOrInside(path, f.Path));

    /// <summary>
    /// Reads the sync roots of one user. Each root is a key named "Provider!SID!Account!N" with the folder's path stored
    /// under UserSyncRoots, one value per user. Two folders of the same app get their folder name added.
    /// </summary>
    internal static List<CloudFolder> FromSyncRoots(RegistryKey roots, string userSid)
    {
        var result = new List<CloudFolder>();
        foreach (string id in roots.GetSubKeyNames())
        {
            using RegistryKey? root = roots.OpenSubKey(id);
            using RegistryKey? users = root?.OpenSubKey("UserSyncRoots");
            if (users?.GetValue(userSid) is not string path || path.Trim().Length == 0) continue;
            string provider = id.Split('!')[0];
            string trimmed = NormalizePath(path);
            if (trimmed.Length == 0) continue;
            result.Add(new CloudFolder(provider, NameOf(provider, root!.GetValue("DisplayNameResource") as string), trimmed));
        }
        return DeduplicateAndLabel(result);
    }

    /// <summary>The name people know the app by; a plain display name the app registered, or the provider's own id.</summary>
    internal static string NameOf(string provider, string? displayName)
    {
        string id = provider.ToLowerInvariant();
        if (id.StartsWith("onedrive")) return "OneDrive";
        if (id.StartsWith("nextcloud")) return "Nextcloud";
        if (id.StartsWith("dropbox")) return "Dropbox";
        if (id.StartsWith("proton")) return "Proton Drive";
        if (id.Contains("googledrive") || id.Contains("drivefs")
            || (!string.IsNullOrWhiteSpace(displayName) && displayName.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)))
            return "Google Drive";
        if (id.StartsWith("icloud")) return "iCloud Drive";
        if (id.StartsWith("box")) return "Box";
        // A name like "@C:\...\app.dll,-101" points into a resource and is no readable name.
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName.Trim();
        return provider;
    }

    /// <summary>
    /// Recognizes a Google Drive volume from its label or filesystem format, including multi-account labels such as
    /// "user@example.com - Google Drive" and "Google Drive File Stream".
    /// </summary>
    internal static CloudFolder? MatchGoogleDriveVolume(string rootPath, string? volumeLabel, string? driveFormat)
    {
        string label = (volumeLabel ?? "").Trim();
        string format = (driveFormat ?? "").Trim();
        bool isGoogleLabel = label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase);
        bool isGoogleFormat = format.Contains("DriveFS", StringComparison.OrdinalIgnoreCase)
            || format.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase);
        if (!isGoogleLabel && !isGoogleFormat) return null;
        string name = FormatGoogleDriveName(label);
        return new CloudFolder("GoogleDrive", name, NormalizePath(rootPath));
    }

    /// <summary>
    /// Extracts Google Drive mount points and mirror folders from the DriveFS PerAccountPreferences JSON and explicit
    /// mount point values.
    /// </summary>
    internal static List<CloudFolder> ParseDriveFsPaths(string? perAccountJson, IEnumerable<string?> explicitPaths, Func<string, bool> exists)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(perAccountJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(perAccountJson);
                CollectDriveFsJsonPaths(document.RootElement, paths);
            }
            catch (JsonException)
            {
            }
        }
        foreach (string? raw in explicitPaths)
        {
            if (NormalizeDriveFsPath(raw) is { } normalized) paths.Add(normalized);
        }
        var result = new List<CloudFolder>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (exists(path)) result.Add(new CloudFolder("GoogleDrive", "Google Drive", path));
        }
        return result;
    }

    private static void CollectDriveFsJsonPaths(JsonElement element, List<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Name is "mount_point_path" or "my_drive_sync_root_path" or "mirror_root_path")
                {
                    if (NormalizeDriveFsPath(property.Value.GetString()) is { } path) paths.Add(path);
                }
                else
                {
                    CollectDriveFsJsonPaths(property.Value, paths);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) CollectDriveFsJsonPaths(item, paths);
        }
    }

    private static string? NormalizeDriveFsPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim().Trim('"').Replace('/', '\\');
        if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0])) return char.ToUpperInvariant(trimmed[0]) + @":\";
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':') return char.ToUpperInvariant(trimmed[0]) + @":\";
        if (!System.IO.Path.IsPathFullyQualified(trimmed)) return null;
        return NormalizePath(trimmed);
    }

    private static string FormatGoogleDriveName(string volumeLabel)
    {
        if (volumeLabel.Length == 0 || string.Equals(volumeLabel, "Google Drive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(volumeLabel, "Google Drive File Stream", StringComparison.OrdinalIgnoreCase))
            return "Google Drive";
        const string suffix = " - Google Drive";
        if (volumeLabel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            string account = volumeLabel[..^suffix.Length].Trim();
            if (account.Length > 0) return $"Google Drive ({account})";
        }
        return volumeLabel;
    }

    private static List<CloudFolder> FromGoogleDriveRegistry()
    {
        try
        {
            using RegistryKey? driveFs = Registry.CurrentUser.OpenSubKey(DriveFsKey);
            using RegistryKey? share = Registry.CurrentUser.OpenSubKey(DriveFsShareKey);
            if (driveFs is null && share is null) return [];
            string? perAccount = driveFs?.GetValue("PerAccountPreferences") as string;
            string?[] explicitPaths =
            [
                driveFs?.GetValue("DefaultMountPoint") as string,
                share?.GetValue("MountPoint") as string,
                share?.GetValue("BasePath") as string,
            ];
            return ParseDriveFsPaths(perAccount, explicitPaths, Directory.Exists);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static List<CloudFolder> FromGoogleDriveNamespace()
    {
        var result = new List<CloudFolder>();
        try
        {
            using RegistryKey? ns = Registry.CurrentUser.OpenSubKey(ExplorerNameSpaceKey);
            using RegistryKey? clsids = Registry.CurrentUser.OpenSubKey(UserClassesClsidKey);
            if (ns is null || clsids is null) return result;
            foreach (string clsid in ns.GetSubKeyNames())
            {
                using RegistryKey? entry = clsids.OpenSubKey(clsid);
                if (entry is null) continue;
                string? display = entry.GetValue("") as string;
                if (string.IsNullOrWhiteSpace(display) || !display.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)) continue;
                using RegistryKey? bag = entry.OpenSubKey(@"Instance\InitPropertyBag");
                string? target = bag?.GetValue("TargetFolderPath") as string;
                if (NormalizeDriveFsPath(target) is { } path && Directory.Exists(path))
                    result.Add(new CloudFolder("GoogleDrive", FormatGoogleDriveName(display.Trim()), path));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return result;
    }

    private static IEnumerable<CloudFolder> GoogleDriveLetters()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string? label = null;
            string? format = null;
            try
            {
                if ((drive.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable) && drive.IsReady)
                {
                    label = drive.VolumeLabel;
                    format = drive.DriveFormat;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            if (MatchGoogleDriveVolume(drive.RootDirectory.FullName, label, format) is { } folder) yield return folder;
        }
    }

    private static List<CloudFolder> DeduplicateAndLabel(List<CloudFolder> items)
    {
        // Keep entries with a more specific name (such as an account suffix from the volume label) when two sources report the same path.
        var byPath = new Dictionary<string, CloudFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (CloudFolder item in items)
        {
            string normalized = NormalizePath(item.Path);
            if (normalized.Length == 0) continue;
            CloudFolder candidate = item with { Path = normalized };
            if (!byPath.TryGetValue(normalized, out CloudFolder? existing) || candidate.Name.Length > existing.Name.Length)
                byPath[normalized] = candidate;
        }
        return byPath.Values
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Count() == 1 ? group : group.Select(f =>
            {
                string suffix = System.IO.Path.GetFileName(f.Path.TrimEnd('\\'));
                if (string.IsNullOrEmpty(suffix)) suffix = f.Path.TrimEnd('\\');
                return f with { Name = $"{f.Name} ({suffix})" };
            }))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Trim().Replace('/', '\\');
        if (normalized.Length == 0) return "";
        string stripped = normalized.TrimEnd('\\');
        if (stripped.Length == 2 && char.IsAsciiLetter(stripped[0]) && stripped[1] == ':')
            return char.ToUpperInvariant(stripped[0]) + @":\";
        return stripped.Length > 0 ? stripped : normalized;
    }
}
