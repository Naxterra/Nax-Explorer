using NaxExplorer.Core;

namespace NaxExplorer;

/// <summary>Appends unexpected exceptions to crash.log in the app's data folder.</summary>
internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DataFolders.Root);
            File.AppendAllText(DataFolders.CrashLog, $"{DateTime.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
