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

    private static bool IsMenuRunning()
    {
        Process[] running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledMenu));
        foreach (Process process in running) process.Dispose();
        return running.Length > 0;
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
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                owner.ShowMessage(Loc.F("PinToNaxStartMenuFailed", ex.Message), InfoBarSeverity.Error);
            }
        };
        menu.Items.Add(start);
    }

    /// <summary>
    /// Starts the menu the way it starts at sign-in, through its own scheduled task, which runs it with the rights its
    /// setup chose and without a UAC prompt. Only when that task is missing is the program itself started.
    /// </summary>
    private static void StartMenu()
    {
        var task = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        task.ArgumentList.Add("/run");
        task.ArgumentList.Add("/tn");
        task.ArgumentList.Add(SignInTask);
        try
        {
            using Process? runner = Process.Start(task);
            if (runner is not null && runner.WaitForExit(10000) && runner.ExitCode == 0) return;
        }
        catch (System.ComponentModel.Win32Exception) { }
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
