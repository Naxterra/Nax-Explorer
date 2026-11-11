using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using NaxExplorer.Core;
using NaxExplorer.Services;
using NaxStartMenu.Integration;

namespace NaxExplorer.Controls;

internal static class StartMenuPinMenu
{
    internal const string MenuTag = "nax-startmenu-pin";
    private static string InstalledMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Nax-StartMenu", "Nax-StartMenu.exe");

    /// <summary>The scheduled task Nax-StartMenu's setup registers to start the menu at sign-in.</summary>
    private const string SignInTask = "Nax-StartMenu";

    internal static void Add(MenuFlyout menu, string path, MainWindow owner)
    {
        if (!StartMenuPinning.CanPin(path) || !File.Exists(InstalledMenu)) return;
        var pin = new MenuFlyoutSubItem { Text = Loc.T("PinToNaxStartMenu"), Tag = MenuTag, Icon = new FontIcon { Glyph = "" } };
        pin.Items.Add(new MenuFlyoutItem { Text = Loc.T("ShellLoading"), IsEnabled = false });
        menu.Items.Add(pin);
        LoadGroups(pin, path, owner);
    }

    private static async void LoadGroups(MenuFlyoutSubItem menu, string path, MainWindow owner)
    {
        try
        {
            // Opening a context menu never starts another program. When the menu is not running, starting it is offered
            // as an entry of its own, so it starts only when the user asks for it.
            if (!await Task.Run(IsMenuRunning))
            {
                ShowStartEntry(menu, owner);
                return;
            }
            PinReply reply = await StartMenuProtocol.SendAsync(new("groups"));
            if (!reply.Success || reply.Groups is null) throw new IOException(reply.Error ?? Loc.T("StartMenuUnavailable"));
            menu.Items.Clear();
            foreach (PinGroup group in reply.Groups)
            {
                var item = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(group.Name) ? "…" : group.Name };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        string registered = await RegisterAsync(path);
                        PinReply pinned = await StartMenuProtocol.SendAsync(new("pin", registered, group.Id));
                        if (!pinned.Success) throw new IOException(pinned.Error ?? Loc.T("StartMenuUnavailable"));
                        owner.ShowMessage(Loc.F("PinnedToNaxStartMenu", group.Name), InfoBarSeverity.Success);
                    }
                    catch (Exception ex) { owner.ShowMessage(Loc.F("PinToNaxStartMenuFailed", ex.Message), InfoBarSeverity.Error); }
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("StartMenuNoGroups"), IsEnabled = false });
        }
        catch (Exception)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("StartMenuUnavailable"), IsEnabled = false });
        }
    }

    /// <summary>
    /// True when the menu runs in this sign-in session. Another signed-in user's menu, in a session of its own, serves
    /// that user's pipe and does not count.
    /// </summary>
    private static bool IsMenuRunning()
    {
        int session;
        using (Process current = Process.GetCurrentProcess()) session = current.SessionId;
        Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledMenu));
        try { return running.Any(process => process.SessionId == session); }
        finally { foreach (Process process in running) process.Dispose(); }
    }

    private static void ShowStartEntry(MenuFlyoutSubItem menu, MainWindow owner)
    {
        menu.Items.Clear();
        var start = new MenuFlyoutItem { Text = Loc.T("StartNaxStartMenu") };
        start.Click += async (_, _) =>
        {
            try
            {
                await Task.Run(StartMenu);
                owner.ShowMessage(Loc.T("NaxStartMenuStarting"), InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                // The click handler is async void, so every failure is shown here instead of ending the app.
                owner.ShowMessage(Loc.F("PinToNaxStartMenuFailed", ex.Message), InfoBarSeverity.Error);
            }
        };
        menu.Items.Add(start);
    }

    /// <summary>
    /// Starts the menu the way it starts at sign-in, through its own scheduled task, which runs it with the rights its
    /// setup chose and without a UAC prompt. The program itself is started only when Windows reports that the task does
    /// not exist; its manifest asks for no elevation ("asInvoker"), so that start shows no UAC prompt either. Any other
    /// failure, such as a task this user may not run, is reported and starts nothing, so two copies never start.
    /// </summary>
    private static void StartMenu()
    {
        const int FileNotFound = unchecked((int)0x80070002);
        object? service = null, folder = null, task = null, running = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            try { task = ((dynamic)folder).GetTask(SignInTask); }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == FileNotFound) { }
            if (task is not null)
            {
                // An empty string runs the task's own action without parameters.
                running = ((dynamic)task).Run("");
                return;
            }
        }
        finally
        {
            foreach (object? com in new[] { running, task, folder, service })
                if (com is not null && System.Runtime.InteropServices.Marshal.IsComObject(com)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com);
        }
        Process.Start(new ProcessStartInfo(InstalledMenu) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(InstalledMenu)! })?.Dispose();
    }

    private static Task<string> RegisterAsync(string path)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(StartMenuPinning.Register(path)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "Nax Start menu shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}
