using System.IO.Enumeration;

namespace NaxExplorer.Core;

/// <summary>A metadata-only size; partial totals are lower bounds rather than exact sizes.</summary>
public sealed record FolderSizeResult(long Bytes, bool IsPartial);

public static class FolderSizeReader
{
    /// <summary>Counts a local folder without following links or fetching online-only directory listings.</summary>
    public static FolderSizeResult? Measure(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FolderStats.IsNetworkPath(path)) return null;
        var pending = new Stack<string>();
        pending.Push(path);
        long bytes = 0;
        bool partial = false;
        bool root = true;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (pending.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested();
            bool atRoot = root;
            root = false;
            try
            {
                FileAttributes attributes = File.GetAttributes(folder);
                if ((attributes & FileAttributes.Directory) == 0 || !FolderStats.Enters(attributes))
                {
                    if (atRoot) return null;
                    partial = true;
                    continue;
                }
                var entries = new FileSystemEnumerable<(string Path, bool Directory, long Size, FileAttributes Attributes)>(folder,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length, entry.Attributes), options);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Directory)
                    {
                        if (FolderStats.Enters(entry.Attributes)) pending.Push(entry.Path);
                        else partial = true;
                    }
                    else bytes = checked(bytes + entry.Size);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (atRoot) return null;
                partial = true;
            }
        }
        return new(bytes, partial);
    }
}
