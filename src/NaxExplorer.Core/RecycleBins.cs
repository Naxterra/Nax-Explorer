using System.Runtime.InteropServices;

namespace NaxExplorer.Core;

/// <summary>Whether a drive has a Recycle Bin, so a delete there can be undone.</summary>
public static class RecycleBins
{
    /// <summary>
    /// True when the drive of <paramref name="path"/> has a Recycle Bin. A network path, a path without a drive and a
    /// drive Windows answers with an error count as "no Recycle Bin", so the app asks before deleting for good rather
    /// than assume an undo that is not there.
    /// </summary>
    public static bool HasRecycleBin(string path)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var info = new QueryInfo { Size = QueryInfoSize };
        return SHQueryRecycleBin(root, ref info) == 0;
    }

    /// <summary>The size Windows expects for its query: 24 bytes on 64-bit Windows, where the struct is not packed.</summary>
    public static int QueryInfoSize => Marshal.SizeOf<QueryInfo>();

    /// <summary>SHQUERYRBINFO. shellapi.h packs it tightly only for 32-bit builds; the app is 64-bit, so it keeps natural alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref QueryInfo info);
}
