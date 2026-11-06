using System.Security;
using Microsoft.Win32;

namespace NaxExplorer.Shell;

/// <summary>
/// The entry under HKEY_CURRENT_USER\...\Run that starts the app at sign-in in the background, while it opens folders
/// instead of Explorer, so Explorer windows other programs open are caught from the start. It names this copy of the
/// app; turning the setting off removes it, and so does the uninstaller.
/// </summary>
internal static class StartAtSignIn
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Nax Explorer";

    public static void Apply(bool on)
    {
        try
        {
            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            string command = $"\"{Environment.ProcessPath}\" {StartArguments.BackgroundSwitch}";
            if (on)
            {
                // Another copy of the app that is still there keeps its entry, as it keeps the folder entries, so
                // starting a second copy never takes the start at sign-in away from the installed one.
                string? current = run.GetValue(ValueName) as string;
                if (current == command || OtherCopy(current) is { } other && File.Exists(other)) return;
                run.SetValue(ValueName, command);
            }
            else if (run.GetValue(ValueName) is string current && current.Contains("Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Only the app's own entry is removed, never one another program put under the same name.
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>The program an existing entry starts when it is a copy of this app, otherwise null.</summary>
    private static string? OtherCopy(string? command)
    {
        if (command is null || !command.StartsWith('"')) return null;
        int end = command.IndexOf('"', 1);
        if (end <= 1) return null;
        string program = command[1..end];
        return program.EndsWith("Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase) ? program : null;
    }
}
