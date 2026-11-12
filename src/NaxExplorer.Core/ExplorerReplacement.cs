using Microsoft.Win32;

namespace NaxExplorer.Core;

/// <summary>
/// Lets Nax Explorer open folders instead of Explorer, for the current user only and only when the user turns it on.
/// Windows opens a folder or drive with the default verb of the "Directory" and "Drive" types, so the app adds its own
/// verb there and makes it the default. Special places such as Home, which Win+E opens on current Windows versions,
/// This PC, the Recycle Bin or Control Panel open through the "open" and "explore" commands of the general "Folder"
/// type, and older versions run the "open new window" command of <see cref="WinEClsid"/> for Win+E; the app points
/// these commands at itself and hands the places it cannot show back to Explorer (see <see cref="ShellPlaces"/>).
/// Everything lives under HKEY_CURRENT_USER\Software\Classes. Whatever was set there before is kept and put
/// back when the setting is turned off or the app is uninstalled, and the machine-wide entries of Explorer are never
/// touched. The desktop, the taskbar's Explorer button and file dialogs stay with Windows.
/// </summary>
public sealed class ExplorerReplacement
{
    public const string Verb = "openinnaxexplorer";

    /// <summary>The shell folder whose "opennewwindow" command Win+E runs on earlier Windows versions.</summary>
    public const string WinEClsid = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    private const string PreviousDefault = "NaxExplorerPreviousDefault";
    private const string Marker = "NaxExplorer";
    private const string MarkerCreated = "created";
    private const string MarkerTaken = "taken";
    private const string PreviousCommand = "NaxExplorerPreviousCommand";
    private const string PreviousDelegate = "NaxExplorerPreviousDelegateExecute";
    private const string OwnCommand = "NaxExplorerCommand";
    private const string CreatedCommand = "NaxExplorerCreatedCommand";

    /// <summary>Written once the app's command is in place, so an unfinished takeover can be told from a later reclaim.</summary>
    private const string Finished = "NaxExplorerFinished";
    private static readonly string[] FolderTypes = ["Directory", "Drive"];
    private static readonly string WinEKey = $@"CLSID\{WinEClsid}\shell\opennewwindow";

    /// <summary>The commands that open every kind of folder, also the special places.</summary>
    private static readonly string[] FolderVerbKeys = [@"Folder\shell\open", @"Folder\shell\explore"];
    private const string ZipVerbKey = @"CompressedFolder\shell\open";

    private readonly RegistryKey _classes;

    /// <param name="classes">HKEY_CURRENT_USER\Software\Classes, opened for writing, or a scratch key in tests.</param>
    public ExplorerReplacement(RegistryKey classes) => _classes = classes;

    /// <summary>The key the app works in for the current user, HKEY_CURRENT_USER\Software\Classes.</summary>
    public static RegistryKey OpenUserClasses() => Registry.CurrentUser.CreateSubKey(@"Software\Classes", writable: true);

    /// <summary>True while folders open with the app's verb; false once another program made its own verb the default.</summary>
    public bool IsDefault()
    {
        using RegistryKey? shell = _classes.OpenSubKey(@"Directory\shell");
        return shell?.GetValue("") as string == Verb;
    }

    /// <summary>The program folders open in, or null when the app has not taken over folder opening.</summary>
    public string? RegisteredProgram()
    {
        using RegistryKey? shell = _classes.OpenSubKey(@"Directory\shell");
        if (shell?.GetValue("") as string != Verb) return null;
        using RegistryKey? command = _classes.OpenSubKey($@"Directory\shell\{Verb}\command");
        if (command?.GetValue("") is not string line || !line.StartsWith('"')) return null;
        int end = line.IndexOf('"', 1);
        return end > 1 ? line[1..end] : null;
    }

    /// <summary>
    /// Makes folders, drives, special places and Win+E open <paramref name="exe"/>; <paramref name="menuText"/> names
    /// the verb in Explorer's own menu. Entries that are already right are left as they are, so the call can run at
    /// every start and only adds what is missing, for example after an update brought new entries. With
    /// <paramref name="repair"/>, as at start, nothing another program has made its own since is taken back. Returns
    /// true when anything was written, so Windows needs to read the entries again.
    /// </summary>
    public bool TurnOn(string exe, string menuText, bool repair = false)
    {
        _changed = false;
        foreach (string type in FolderTypes)
        {
            using RegistryKey shell = _classes.CreateSubKey($@"{type}\shell", writable: true);
            // At start, a folder type whose default verb another program has made its own since stays with it.
            if (repair && shell.GetValue("") is string current && current.Length > 0 && current != Verb) continue;
            using RegistryKey verb = shell.CreateSubKey(Verb, writable: true);
            if (shell.GetValue("") is string previous && previous != Verb) Write(verb, PreviousDefault, previous);
            Write(verb, "", menuText);
            using (RegistryKey command = verb.CreateSubKey("command", writable: true)) Write(command, "", $"\"{exe}\" \"%1\"");
            Write(shell, "", Verb);
        }
        TakeCommand(WinEKey, $"\"{exe}\"");
        foreach (string key in FolderVerbKeys) TakeCommand(key, $"\"{exe}\" \"%1\"");
        // ZIP's own open command takes precedence over Folder. Route it directly instead of catching and closing
        // the Explorer window afterwards. Other archive handlers, such as WinRAR.ZIP, retain their associations.
        TakeCommand(ZipVerbKey, $"\"{exe}\" \"%1\"");
        return _changed;
    }

    /// <summary>Set by <see cref="Write"/> when a value really changed.</summary>
    private bool _changed;

    private void Write(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) is string current && current == value) return;
        key.SetValue(name, value);
        _changed = true;
    }

    /// <summary>
    /// Points the command of a verb at the app. What was there before is noted on the verb's key and comes back when
    /// the app lets go. A command another program wrote after the app is never overwritten, also when the app repairs
    /// its own entries; a takeover that stopped before the command was written still shows the command the app noted
    /// as the earlier one, and is finished here.
    /// </summary>
    private void TakeCommand(string verbKey, string own)
    {
        bool existed;
        using (RegistryKey? found = _classes.OpenSubKey(verbKey)) existed = found is not null;
        using RegistryKey verb = _classes.CreateSubKey(verbKey, writable: true);
        if (verb.GetValue(Marker) is null)
        {
            if (existed)
            {
                using RegistryKey? before = verb.OpenSubKey("command");
                if (before?.GetValue("") is string command) Write(verb, PreviousCommand, command);
                if (before?.GetValue("DelegateExecute") is string delegateExecute) Write(verb, PreviousDelegate, delegateExecute);
                if (before is null) Write(verb, CreatedCommand, "1");
            }
            Write(verb, Marker, existed ? MarkerTaken : MarkerCreated);
        }
        else if (!StillOwn(verb) && !(verb.GetValue(Finished) is null && UnchangedSinceNoted(verb)))
        {
            // Another program wrote its command after the app's takeover was finished, even if it is the same command
            // it had before; that choice stays.
            return;
        }
        // What the app writes is noted first, so that even after a failure halfway, letting go recognizes the
        // command as the app's own and never removes one another program put there later.
        Write(verb, OwnCommand, own);
        using RegistryKey commandKey = verb.CreateSubKey("command", writable: true);
        Write(commandKey, "", own);
        // An empty DelegateExecute makes Windows run the command instead of Explorer's own handler.
        Write(commandKey, "DelegateExecute", "");
        Write(verb, Finished, "1");
    }

    /// <summary>True when a command is still the one that was there before the app took over, or still none.</summary>
    private static bool UnchangedSinceNoted(RegistryKey verb)
    {
        string? current;
        using (RegistryKey? command = verb.OpenSubKey("command")) current = command?.GetValue("") as string;
        return current == verb.GetValue(PreviousCommand) as string;
    }

    /// <summary>
    /// True while a command is still the one the app wrote. A build from before the app noted its command wrote only
    /// the Win+E command, so without the note a command that runs Nax-Explorer.exe counts as the app's own.
    /// </summary>
    private static bool StillOwn(RegistryKey verb)
    {
        string? current;
        using (RegistryKey? command = verb.OpenSubKey("command")) current = command?.GetValue("") as string;
        if (current is null) return false;
        return verb.GetValue(OwnCommand) is string own
            ? current == own
            : current.Contains("Nax-Explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gives folder opening and Win+E back to Explorer and removes everything the app wrote.</summary>
    public void TurnOff()
    {
        foreach (string type in FolderTypes)
        {
            using (RegistryKey? shell = _classes.OpenSubKey($@"{type}\shell", writable: true))
            {
                if (shell is null) continue;
                string? previous;
                using (RegistryKey? verb = shell.OpenSubKey(Verb)) previous = verb?.GetValue(PreviousDefault) as string;
                if (shell.GetValue("") as string == Verb)
                {
                    if (previous is null) shell.DeleteValue("", throwOnMissingValue: false);
                    else shell.SetValue("", previous);
                }
                shell.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
            }
            DeleteIfEmpty($@"{type}\shell");
            DeleteIfEmpty(type);
        }
        ReleaseCommand(WinEKey, $@"CLSID\{WinEClsid}\shell", $@"CLSID\{WinEClsid}", "CLSID");
        foreach (string key in FolderVerbKeys) ReleaseCommand(key, @"Folder\shell", "Folder");
        ReleaseCommand(ZipVerbKey, @"CompressedFolder\shell", "CompressedFolder");
    }

    /// <summary>
    /// Gives one command back: a key the app made is removed with any parent key that is empty then, a command the app
    /// replaced is put back, and a command another program wrote after the app stays, so only the app's notes go.
    /// </summary>
    private void ReleaseCommand(string verbKey, params string[] emptyParents)
    {
        string? marker;
        using (RegistryKey? verb = _classes.OpenSubKey(verbKey, writable: true))
        {
            marker = verb?.GetValue(Marker) as string;
            if (verb is not null && marker is not null)
            {
                bool createdCommand = verb.GetValue(CreatedCommand) is not null;
                if (!StillOwn(verb))
                {
                    marker = null;
                }
                else if (marker == MarkerTaken)
                {
                    using (RegistryKey command = verb.CreateSubKey("command", writable: true))
                    {
                        Restore(command, "", verb.GetValue(PreviousCommand) as string);
                        Restore(command, "DelegateExecute", verb.GetValue(PreviousDelegate) as string);
                    }
                    if (createdCommand) DeleteIfEmpty(verbKey + @"\command");
                }
                if (marker != MarkerCreated)
                {
                    foreach (string note in new[] { PreviousCommand, PreviousDelegate, OwnCommand, CreatedCommand, Finished, Marker }) verb.DeleteValue(note, throwOnMissingValue: false);
                }
            }
        }
        if (marker != MarkerCreated) return;
        _classes.DeleteSubKeyTree(verbKey, throwOnMissingSubKey: false);
        foreach (string parent in emptyParents) DeleteIfEmpty(parent);
    }

    private static void Restore(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value);
    }

    private void DeleteIfEmpty(string path)
    {
        using (RegistryKey? key = _classes.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        _classes.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
