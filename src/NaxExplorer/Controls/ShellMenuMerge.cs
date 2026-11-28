using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NaxExplorer.Services;
using NaxExplorer.Shell;

namespace NaxExplorer.Controls;

/// <summary>
/// Puts Windows and third-party commands directly into the app's menu before it opens. A slow shell extension delays
/// opening the menu without blocking the window; it never rearranges an open menu or adds another command layer.
/// </summary>
internal static class ShellMenuMerge
{
    /// <summary>Marks the app's own items, so Windows' entries go in the right place.</summary>
    public const string PropertiesTag = "properties";
    public const string NewTag = "new";
    public const string OpenWithTag = "openwith";
    public const string ExtractTag = "extract";

    public static async void Show(
        MenuFlyout menu,
        FrameworkElement target,
        Windows.Foundation.Point position,
        Task<(ShellMenuSession Session, List<ShellMenuEntry> Entries)?> loading,
        MainWindow ownerWindow,
        bool background)
    {
        IntPtr owner = ownerWindow.Hwnd;
        menu.AreOpenCloseAnimationsEnabled = false;
        var options = new FlyoutShowOptions { Position = position };
        ShellMenuSession? session = null;
        menu.Closed += (_, _) => session?.Close();
        using var cancelled = new CancellationTokenSource();
        bool waitingToOpen = true;
        UIElement root = target.XamlRoot.Content;
        void Cancel() { if (waitingToOpen) cancelled.Cancel(); }
        PointerEventHandler pointer = (_, _) => Cancel();
        KeyEventHandler key = (_, _) => Cancel();
        void Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) Cancel();
        }
        void Unloaded(object sender, RoutedEventArgs args) => Cancel();
        root.AddHandler(UIElement.PointerPressedEvent, pointer, handledEventsToo: true);
        root.AddHandler(UIElement.PreviewKeyDownEvent, key, handledEventsToo: true);
        ownerWindow.Activated += Activated;
        target.Unloaded += Unloaded;
        void StopWaiting()
        {
            if (!waitingToOpen) return;
            waitingToOpen = false;
            root.RemoveHandler(UIElement.PointerPressedEvent, pointer);
            root.RemoveHandler(UIElement.PreviewKeyDownEvent, key);
            ownerWindow.Activated -= Activated;
            target.Unloaded -= Unloaded;
        }
        try
        {
            await ContextMenuPresentation.ShowAsync(loading,
                isCurrent: () => target.IsLoaded && Native.GetForegroundWindow() == owner,
                showReady: ready =>
                {
                    session = ready?.Session;
                    if (ready is { } result) Merge(menu, result.Session, result.Entries, owner, background);
                    StopWaiting();
                    menu.ShowAt(target, options);
                },
                discard: result => result?.Session.Close(), token: cancelled.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session?.Close();
            CrashLog.Write(ex);
        }
        finally
        {
            StopWaiting();
        }
    }

    private static int IndexOf(MenuFlyout menu, string tag)
    {
        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].Tag as string == tag) return i;
        }
        return menu.Items.Count;
    }

    private static void Merge(MenuFlyout menu, ShellMenuSession session, List<ShellMenuEntry> entries, IntPtr owner, bool background)
    {
        if (background)
        {
            // From the background menu only Windows' "New" submenu is taken: it lists every installed file template.
            // Its other entries (view, sort, group) need Explorer's own folder view and do nothing elsewhere.
            ShellMenuEntry? newMenu = entries.FirstOrDefault(e => e.Children.Any(c => string.Equals(c.Verb, "NewFolder", StringComparison.OrdinalIgnoreCase)));
            if (newMenu is null || menu.Items.FirstOrDefault(i => i.Tag as string == NewTag) is not MenuFlyoutSubItem own) return;
            (_, List<ShellMenuEntry> keptTemplates) = Core.ContextMenuFilter.FilterAndExtractOpenWith(
                newMenu.Children,
                e => e.Separator,
                e => e.Verb,
                e => e.Text,
                e => e.Enabled,
                e => e.Children,
                (e, children) => e with { Children = children },
                promoteOpenWith: false,
                hideWindowsStartPin: false,
                hideWindowsExtract: false);
            List<MenuFlyoutItemBase> templates = Convert(keptTemplates, session, owner);
            if (templates.Count == 0) return;
            own.Items.Clear();
            foreach (MenuFlyoutItemBase template in templates) own.Items.Add(template);
            return;
        }
        int openWithIndex = IndexOf(menu, OpenWithTag);
        bool hasOpenWithSlot = openWithIndex < menu.Items.Count;
        (ShellMenuEntry? promotedOpenWith, List<ShellMenuEntry> remaining) = Core.ContextMenuFilter.FilterAndExtractOpenWith(
            entries,
            e => e.Separator,
            e => e.Verb,
            e => e.Text,
            e => e.Enabled,
            e => e.Children,
            (e, children) => e with { Children = children },
            promoteOpenWith: hasOpenWithSlot,
            hideWindowsStartPin: menu.Items.Any(item => item.Tag as string == StartMenuPinMenu.MenuTag),
            hideWindowsExtract: menu.Items.Any(item => item.Tag as string == ExtractTag));
        if (promotedOpenWith is not null && hasOpenWithSlot)
        {
            MenuFlyoutItemBase openWithItem = ConvertOne(promotedOpenWith, session, owner, fallbackGlyph: "\uE7AC");
            menu.Items[openWithIndex] = openWithItem;
        }
        List<MenuFlyoutItemBase> items = Convert(remaining, session, owner);
        if (items.Count == 0) return;
        // Windows' entries go between the app's commands and Properties, with one line on each side: the line the
        // app's menu already has above Properties is used, and a new one goes below Windows' entries.
        int at = IndexOf(menu, PropertiesTag);
        if (at == 0 || menu.Items[at - 1] is not MenuFlyoutSeparator) menu.Items.Insert(at++, new MenuFlyoutSeparator());
        foreach (MenuFlyoutItemBase item in items) menu.Items.Insert(at++, item);
        if (at < menu.Items.Count) menu.Items.Insert(at, new MenuFlyoutSeparator());
    }

    /// <summary>Turns already-filtered Windows shell entries into WinUI menu items.</summary>
    internal static List<MenuFlyoutItemBase> Convert(IEnumerable<ShellMenuEntry> entries, ShellMenuSession session, IntPtr owner)
    {
        var result = new List<MenuFlyoutItemBase>();
        foreach (ShellMenuEntry entry in entries)
        {
            if (entry.Separator)
            {
                if (result.Count > 0 && result[^1] is not MenuFlyoutSeparator) result.Add(new MenuFlyoutSeparator());
                continue;
            }
            result.Add(ConvertOne(entry, session, owner));
        }
        while (result.Count > 0 && result[^1] is MenuFlyoutSeparator) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static MenuFlyoutItemBase ConvertOne(ShellMenuEntry entry, ShellMenuSession session, IntPtr owner, string? fallbackGlyph = null)
    {
        IconElement? icon = entry.Icon is { } pixels
            ? new ImageIcon { Source = ImageCache.ToBitmap(pixels) }
            : fallbackGlyph is not null ? new FontIcon { Glyph = fallbackGlyph } : null;
        if (entry.Children.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = entry.Text, Icon = icon, IsEnabled = entry.Enabled };
            foreach (MenuFlyoutItemBase child in Convert(entry.Children, session, owner)) sub.Items.Add(child);
            return sub;
        }
        var item = new MenuFlyoutItem { Text = entry.Text, IsEnabled = entry.Enabled, Icon = icon };
        uint id = entry.Id;
        item.Click += (_, _) =>
        {
            Native.GetCursorPos(out Native.POINT point);
            session.Invoke(id, owner, point);
        };
        return item;
    }
}
