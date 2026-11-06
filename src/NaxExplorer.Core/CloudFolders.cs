using System.Security.Principal;
using Microsoft.Win32;

namespace NaxExplorer.Core;

/// <summary>A folder that a cloud sync app keeps in step with its online storage.</summary>
public sealed record CloudFolder(string Provider, string Name, string Path);

/// <summary>
/// Finds the folders of cloud sync apps on this PC. Apps that use Windows' cloud files platform, such as OneDrive,
/// Nextcloud, Dropbox and Proton Drive, register each folder as a "sync root"; Google Drive for desktop shows its files
/// on a drive of its own named "Google Drive". The app only reads where these folders are; the sync apps keep doing
/// the syncing.
/// </summary>
public static class CloudFolders
{
    private const string SyncRootManager = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>
    /// The cloud folders of the current user. With <paramref name="withDrives"/>, drives are asked for their label to
    /// find Google Drive, which can be slow for a network drive, so callers on the UI thread leave it out.
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
        if (withDrives) found.AddRange(GoogleDriveLetters());
        return found.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
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
            string trimmed = path.Trim();
            trimmed = trimmed.Length > 3 ? trimmed.TrimEnd('\\') : trimmed;
            result.Add(new CloudFolder(provider, NameOf(provider, root!.GetValue("DisplayNameResource") as string), trimmed));
        }
        return result
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Count() == 1 ? group : group.Select(f => f with { Name = $"{f.Name} ({System.IO.Path.GetFileName(f.Path)})" }))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The name people know the app by; a plain display name the app registered, or the provider's own id.</summary>
    internal static string NameOf(string provider, string? displayName)
    {
        string id = provider.ToLowerInvariant();
        if (id.StartsWith("onedrive")) return "OneDrive";
        if (id.StartsWith("nextcloud")) return "Nextcloud";
        if (id.StartsWith("dropbox")) return "Dropbox";
        if (id.StartsWith("proton")) return "Proton Drive";
        if (id.StartsWith("googledrive") || id.StartsWith("drivefs")) return "Google Drive";
        if (id.StartsWith("icloud")) return "iCloud Drive";
        if (id.StartsWith("box")) return "Box";
        // A name like "@C:\...\app.dll,-101" points into a resource and is no readable name.
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName.Trim();
        return provider;
    }

    private static IEnumerable<CloudFolder> GoogleDriveLetters()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string? label = null;
            try
            {
                if ((drive.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable) && drive.IsReady) label = drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            if (string.Equals(label, "Google Drive", StringComparison.OrdinalIgnoreCase)) yield return new CloudFolder("GoogleDrive", "Google Drive", drive.RootDirectory.FullName);
        }
    }
}
