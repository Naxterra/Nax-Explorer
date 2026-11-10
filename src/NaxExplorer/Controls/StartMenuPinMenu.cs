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

    internal static void Add(MenuFlyout menu, string path, MainWindow owner)
    {
        if (!StartMenuPinning.CanPin(path) || !File.Exists(InstalledMenu)) return;
        var pin = new MenuFlyoutSubItem { Text = Loc.T("PinToNaxStartMenu"), Tag = MenuTag, Icon = new FontIcon { Glyph = "" } };
        pin.Items.Add(new MenuFlyoutItem { Text = Loc.T("ShellLoading"), IsEnabled = false });
        menu.Items.Add(pin);
        LoadGroups(pin, path, owner);
    }

    private static async void LoadGroups(MenuFlyoutSubItem menu, string path, MainWindow owner)
    {
        try
        {
            PinReply reply;
            try { reply = await StartMenuProtocol.SendAsync(new("groups")); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // A stopped menu starts through its normal launcher/task; no elevated file writes from Explorer.
                Process.Start(new ProcessStartInfo(InstalledMenu) { UseShellExecute = true });
                reply = await StartMenuProtocol.SendAsync(new("groups"));
            }
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
