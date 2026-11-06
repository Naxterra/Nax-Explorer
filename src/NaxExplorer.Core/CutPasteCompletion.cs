namespace NaxExplorer.Core;

/// <summary>The clipboard version and sources captured before a cut-paste starts.</summary>
public sealed class CutPasteCompletion(IReadOnlyList<string> sources, uint clipboardVersion)
{
    /// <summary>A failed, cancelled, or partly skipped move keeps the cut selection available for retry.</summary>
    public bool AllMoved(bool succeeded)
    {
        if (!succeeded || sources.Count == 0) return false;
        foreach (string source in sources)
        {
            try
            {
                File.GetAttributes(source);
                return false; // A skipped file, or a merged folder with items left behind.
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false; // An inaccessible source is not proof that it moved.
            }
        }
        return true;
    }

    /// <summary>Checked while holding the native clipboard open, so a later Copy is never erased.</summary>
    public bool OwnsClipboard(uint currentVersion) => clipboardVersion != 0 && clipboardVersion == currentVersion;
}
