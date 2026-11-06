using NaxExplorer.Core;

namespace NaxExplorer.Services;

/// <summary>
/// The cloud sync folders found on this PC, shared by the sidebar and the lists. The folders registered with Windows
/// are read at start, which takes a moment; drives are asked later in the background (see <see cref="CloudFolders"/>).
/// </summary>
internal static class CloudRoots
{
    private static volatile IReadOnlyList<CloudFolder> _folders = CloudFolders.Find(withDrives: false);

    public static IReadOnlyList<CloudFolder> Folders => _folders;

    public static void Set(IReadOnlyList<CloudFolder> folders) => _folders = folders;

    public static bool Contains(string path) => CloudFolders.Contains(_folders, path);
}
