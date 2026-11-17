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

    /// <summary>
    /// Commands the app's own menu already has, and commands that open Explorer windows. Windows' versions of them are
    /// left out, so no command appears twice. The app's own "open in Nax Explorer" verb, which it adds to Explorer's
    /// menu while it opens folders instead of Explorer, is the same as "open" here.
    /// </summary>
    private static readonly HashSet<string> OwnVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennew", "explore", "opennewwindow", "opennewprocess", "opennewtab", "cut", "copy", "paste", "delete",
        "rename", "properties", "copyaspath", "pintohome", "find", "refresh", "undo", "redo", Core.ExplorerReplacement.Verb,
    };

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
            List<MenuFlyoutItemBase> templates = Convert(newMenu.Children, session, owner);
            if (templates.Count == 0) return;
            own.Items.Clear();
            foreach (MenuFlyoutItemBase template in templates) own.Items.Add(template);
            return;
        }
        var shownVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<MenuFlyoutItemBase> items = Convert(entries, session, owner, shownVerbs,
            hideWindowsStartPin: menu.Items.Any(item => item.Tag as string == StartMenuPinMenu.MenuTag),
            hideWindowsExtract: menu.Items.Any(item => item.Tag as string == ExtractTag));
        if (items.Count == 0) return;
        // Windows' entries go between the app's commands and Properties, with one line on each side: the line the
        // app's menu already has above Properties is used, and a new one goes below Windows' entries.
        int at = IndexOf(menu, PropertiesTag);
        if (at == 0 || menu.Items[at - 1] is not MenuFlyoutSeparator) menu.Items.Insert(at++, new MenuFlyoutSeparator());
        foreach (MenuFlyoutItemBase item in items) menu.Items.Insert(at++, item);
        if (at < menu.Items.Count) menu.Items.Insert(at, new MenuFlyoutSeparator());
        // Windows' "Open with" (its submenu with the apps, or a single entry) ends in the verb "openas". Only when such
        // an entry is really shown and usable does the app's own "Open with" give way, so the menu never loses it.
        if (shownVerbs.Contains("openas") && menu.Items.FirstOrDefault(i => i.Tag as string == OpenWithTag) is { } ownOpenWith) menu.Items.Remove(ownOpenWith);
    }

    /// <summary>
    /// Turns Windows' entries into menu items, without duplicates of the app's commands and without stray separators.
    /// The verbs of the enabled items that are shown are added to <paramref name="shownVerbs"/>.
    /// </summary>
    internal static List<MenuFlyoutItemBase> Convert(IEnumerable<ShellMenuEntry> entries, ShellMenuSession session, IntPtr owner, ISet<string>? shownVerbs = null, bool hideWindowsStartPin = false, bool hideWindowsExtract = false)
    {
        var result = new List<MenuFlyoutItemBase>();
        foreach (ShellMenuEntry entry in entries)
        {
            if (entry.Separator)
            {
                if (result.Count > 0 && result[^1] is not MenuFlyoutSeparator) result.Add(new MenuFlyoutSeparator());
                continue;
            }
            if (entry.Verb is { } verb && OwnVerbs.Contains(verb)) continue;
            if (hideWindowsStartPin && string.Equals(entry.Verb, "pintostartscreen", StringComparison.OrdinalIgnoreCase)) continue;
            if (hideWindowsExtract && (string.Equals(entry.Verb, "extract", StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Verb, "extractall", StringComparison.OrdinalIgnoreCase))) continue;
            if (entry.Text.Length == 0) continue;
            IconElement? icon = entry.Icon is { } pixels ? new ImageIcon { Source = ImageCache.ToBitmap(pixels) } : null;
            if (entry.Children.Count > 0)
            {
                List<MenuFlyoutItemBase> children = Convert(entry.Children, session, owner, shownVerbs, hideWindowsStartPin, hideWindowsExtract);
                if (children.Count == 0) continue;
                var sub = new MenuFlyoutSubItem { Text = entry.Text, Icon = icon };
                foreach (MenuFlyoutItemBase child in children) sub.Items.Add(child);
                result.Add(sub);
                continue;
            }
            var item = new MenuFlyoutItem { Text = entry.Text, IsEnabled = entry.Enabled, Icon = icon };
            if (entry.Enabled && entry.Verb is { } shownVerb) shownVerbs?.Add(shownVerb);
            uint id = entry.Id;
            item.Click += (_, _) =>
            {
                Native.GetCursorPos(out Native.POINT point);
                session.Invoke(id, owner, point);
            };
            result.Add(item);
        }
        while (result.Count > 0 && result[^1] is MenuFlyoutSeparator) result.RemoveAt(result.Count - 1);
        return result;
    }
}
