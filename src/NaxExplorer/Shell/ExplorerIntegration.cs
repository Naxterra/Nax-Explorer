using System.Security;
using Microsoft.Win32;
using NaxExplorer.Core;
using NaxExplorer.Services;

namespace NaxExplorer.Shell;

/// <summary>Turns the opt-in "open folders in Nax Explorer" setting into the registry entries of <see cref="ExplorerReplacement"/>.</summary>
internal static class ExplorerIntegration
{
    /// <summary>
    /// Takes over folder opening and Win+E, or gives them back to Explorer, and tells Windows about the change. With
    /// <paramref name="repair"/>, as at start, nothing another program made its own is taken back, and a failure
    /// leaves everything as it is for the next start instead of letting go.
    /// </summary>
    public static bool Apply(bool on, string? exe = null, bool repair = false)
    {
        try
        {
            using RegistryKey classes = ExplorerReplacement.OpenUserClasses();
            var replacement = new ExplorerReplacement(classes);
            bool changed = true;
            if (on) changed = replacement.TurnOn(exe ?? Environment.ProcessPath!, Loc.T("OpenInNaxExplorer"), repair);
            else replacement.TurnOff();
            // Windows reads the entries again only when something changed; a start with everything in place costs nothing.
            if (changed) Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            CrashLog.Write(ex);
            // When the user turned it on, a takeover that stopped halfway is taken back, so the switch, which goes back
            // to off, tells the truth. A routine repair at start that fails changes nothing more and tries again later.
            if (on && !repair) TryTurnOff();
            return false;
        }
    }

    private static void TryTurnOff()
    {
        try
        {
            using RegistryKey classes = ExplorerReplacement.OpenUserClasses();
            new ExplorerReplacement(classes).TurnOff();
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// At start, while the setting is on. When another program has made itself the one that opens folders since, that
    /// newer choice wins: the app lets go and the setting turns off. Otherwise the entries are brought up to date: a
    /// moved app is pointed at this copy, and entries a newer version adds, such as the special places, are written.
    /// </summary>
    public static void Repair(AppSettings settings)
    {
        if (!settings.ReplaceExplorer) return;
        bool ours;
        string? registered;
        try
        {
            using RegistryKey classes = ExplorerReplacement.OpenUserClasses();
            var replacement = new ExplorerReplacement(classes);
            ours = replacement.IsDefault();
            registered = replacement.RegisteredProgram();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            CrashLog.Write(ex);
            return;
        }
        if (!ours)
        {
            TryTurnOff();
            settings.ReplaceExplorer = false;
            App.Instance?.SaveSettings();
            return;
        }
        // Writes only what is missing or wrong, for the copy the entries name as long as it exists, so starting a second
        // copy of the app never takes the entries over; a command another program took over since is left to it.
        Apply(true, registered is not null && File.Exists(registered) ? registered : null, repair: true);
    }
}
