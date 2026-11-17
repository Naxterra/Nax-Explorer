using NaxExplorer.Services;

namespace NaxExplorer.Tests;

internal static class ContextMenuTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("ready_context_menu_opens_once_with_its_commands", "Commands ready before opening are shown in one complete menu without a loading menu or later replacement.", () =>
        {
            var events = new List<string>();
            ContextMenuPresentation.ShowAsync(Task.FromResult("commands"), TimeSpan.Zero,
                () => true, value => events.Add("open:" + value), () => events.Add("pending"),
                () => true, _ => events.Add("fill"), _ => events.Add("discard")).GetAwaiter().GetResult();
            Check.Sequence(["open:commands"], events);
        });
        yield return ("slow_context_menu_commands_fill_a_submenu_without_reopening", "A slow shell extension opens the base menu once and delivers its commands only to the reserved submenu, preserving the visible main menu and command positions.", () => SlowMenu().GetAwaiter().GetResult());
        yield return ("dismissed_context_menu_releases_late_shell_commands", "Closing the menu before Windows finishes releases the late shell session without changing or reopening the dismissed menu.", () => ClosedMenu().GetAwaiter().GetResult());
        yield return ("superseded_context_menu_never_opens_after_loading", "A click or keypress that supersedes a pending right-click request prevents its completed shell menu from appearing later.", () => SupersededMenu().GetAwaiter().GetResult());
    }

    private static async Task SlowMenu()
    {
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new List<string>();
        var children = new List<string>();
        int opens = 0;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, TimeSpan.Zero, () => true,
            _ => { opens++; root.Add("unexpected replacement"); },
            () => { opens++; root.AddRange(["Open", "Copy", "More commands", "Properties"]); },
            () => true, value => children.Add(value), _ => throw new Exception("open menu discarded its commands"));
        Check.Equal(1, opens, "base menu did not open while the extension was loading");
        loading.SetResult("extension commands");
        await showing;
        Check.Equal(1, opens, "menu opened a second time");
        Check.Sequence(["Open", "Copy", "More commands", "Properties"], root);
        Check.Sequence(["extension commands"], children);
    }

    private static async Task ClosedMenu()
    {
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0, fills = 0, releases = 0;
        bool open = true;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, TimeSpan.Zero, () => true,
            _ => opens++, () => opens++, () => open, _ => fills++, _ => releases++);
        open = false;
        loading.SetResult("closed session");
        await showing;
        Check.Equal(1, opens);
        Check.Equal(0, fills);
        Check.Equal(1, releases);
    }

    private static async Task SupersededMenu()
    {
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        int opens = 0, fills = 0, releases = 0;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, TimeSpan.FromSeconds(30), () => current,
            _ => opens++, () => opens++, () => true, _ => fills++, _ => releases++);
        current = false;
        loading.SetResult("superseded session");
        await showing;
        Check.Equal(0, opens);
        Check.Equal(0, fills);
        Check.Equal(1, releases);
    }
}
