namespace NaxExplorer.Core;

/// <summary>Where the content of an item of a cloud sync folder is.</summary>
public enum CloudState
{
    None,
    OnlineOnly,
    OnDevice,
    AlwaysOnDevice,
}

/// <summary>
/// Files of cloud sync apps such as Nextcloud, OneDrive, Google Drive, Proton Drive or Dropbox can live only online:
/// Windows shows them as normal files, but their content is downloaded when a program reads it. The app never reads
/// such a file on its own, not to search its contents, index it, preview it or make a thumbnail; only opening it,
/// which the user asks for, downloads it.
/// </summary>
public static class CloudFiles
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the content is fetched from the cloud when it is read.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: the item is fetched from the cloud when it is opened.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>FILE_ATTRIBUTE_PINNED: the user chose "always keep on this device".</summary>
    public const FileAttributes Pinned = (FileAttributes)0x00080000;

    /// <summary>
    /// What the list shows for an item of a cloud sync folder, as Explorer does: only online, on this device, or always
    /// kept on this device. Items outside cloud folders show nothing unless their attributes say they live online.
    /// Folders show only "always kept", because whether all their files are on the device is not known without reading
    /// the whole folder.
    /// </summary>
    public static CloudState StateOf(FileAttributes attributes, bool inCloudFolder)
    {
        if (IsOnlineOnly(attributes)) return CloudState.OnlineOnly;
        if ((attributes & Pinned) != 0) return CloudState.AlwaysOnDevice;
        if ((attributes & FileAttributes.Directory) != 0) return CloudState.None;
        return inCloudFolder ? CloudState.OnDevice : CloudState.None;
    }

    /// <summary>True for a cloud folder whose list of files is not on this device yet; looking into it makes the sync app fetch the list.</summary>
    public static bool IsUnfetchedFolder(FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) != 0 && (attributes & (RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>The same for a path, read now; a folder that cannot be asked counts as not unfetched.</summary>
    public static bool IsUnfetchedFolder(string path)
    {
        try
        {
            return IsUnfetchedFolder(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True for a file whose content is not on this device, so reading it would download it.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & FileAttributes.Directory) == 0 && (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    /// <summary>The same for a path; a file that cannot be asked counts as not online-only, as nothing can read it anyway.</summary>
    public static bool IsOnlineOnly(string path)
    {
        try
        {
            return IsOnlineOnly(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
