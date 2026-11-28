using NaxExplorer.Core;
using NaxExplorer.Services;

namespace NaxExplorer.Tests;

internal static class ContextMenuTests
{
    private sealed record FakeEntry(string Text, string? Verb = null, bool Enabled = true, bool Separator = false, IReadOnlyList<FakeEntry>? Children = null)
    {
        public IReadOnlyList<FakeEntry> ChildList => Children ?? [];
    }

    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("shell_menu_filter_promotes_open_with_hides_create_shortcut_and_preserves_google_drive", "Windows Open With is promoted out of the shell section to replace the top Open With slot, Create shortcut ('link') is omitted without stray separators, and Google Drive sharing entries stay intact.", () =>
        {
            var raw = new List<FakeEntry>
            {
                new("Öffnen", "open"),
                new("WinRAR", Children: [new("Zum Archiv hinzufügen...")]),
                new("", Separator: true),
                new("Mit Google Drive öffnen"),
                new("Link in die Zwischenablage kopieren"),
                new("In Google Drive freigeben"),
                new("Verknüpfung zu Google Drive hinzufügen"),
                new("Offlinezugriff", Children: [new("Offline verfügbar"), new("Nur online")]),
                new("", Separator: true),
                new("Öffnen mit", Children:
                [
                    new("PotPlayer"),
                    new("", Separator: true),
                    new("Andere App auswählen", "openas"),
                ]),
                new("", Separator: true),
                new("Senden an", Children: [new("Bluetooth")]),
                new("", Separator: true),
                new("Verknüpfung erstellen", "link"),
                new("Löschen", "delete"),
                new("Eigenschaften", "properties"),
            };

            var (promoted, remaining) = ContextMenuFilter.FilterAndExtractOpenWith(
                raw,
                e => e.Separator,
                e => e.Verb,
                e => e.Text,
                e => e.Enabled,
                e => e.ChildList,
                (e, children) => e with { Children = children },
                promoteOpenWith: true,
                hideWindowsStartPin: false,
                hideWindowsExtract: false);

            Check.That(promoted is not null && promoted.Text == "Öffnen mit" && promoted.ChildList.Count == 3, "Open With submenu must be extracted for top placement");
            Check.Sequence(
                ["WinRAR", "---", "Mit Google Drive öffnen", "Link in die Zwischenablage kopieren", "In Google Drive freigeben", "Verknüpfung zu Google Drive hinzufügen", "Offlinezugriff", "---", "Senden an"],
                remaining.Select(e => e.Separator ? "---" : e.Text),
                "Create shortcut and duplicates must be stripped, Open With removed from bottom, and Google Drive items preserved");

            // Disabled Open With must not replace the app's working fallback.
            var disabledOpenWith = new List<FakeEntry>
            {
                new("Öffnen mit", Enabled: false, Children: [new("Andere App auswählen", "openas", Enabled: true)]),
            };
            var (notPromoted, disabledRemaining) = ContextMenuFilter.FilterAndExtractOpenWith(
                disabledOpenWith,
                e => e.Separator, e => e.Verb, e => e.Text, e => e.Enabled, e => e.ChildList,
                (e, children) => e with { Children = children },
                promoteOpenWith: true, hideWindowsStartPin: false, hideWindowsExtract: false);
            Check.That(notPromoted is null, "disabled Open With submenu must not replace working fallback");
            Check.Equal(0, disabledRemaining.Count, "disabled Open With submenu must not duplicate the top Open With slot");
        });
        yield return ("ready_context_menu_opens_once_with_its_commands", "Commands ready before opening are shown in one complete menu without a loading menu or later replacement.", () =>
        {
            var events = new List<string>();
            ContextMenuPresentation.ShowAsync(Task.FromResult("commands"),
                () => true, value => events.Add("open:" + value), _ => events.Add("discard")).GetAwaiter().GetResult();
            Check.Sequence(["open:commands"], events);
        });
        yield return ("slow_context_menu_opens_once_with_all_commands_together", "A slow shell extension is awaited without opening a partial menu; its commands are included directly in the single menu shown afterward.", () => SlowMenu().GetAwaiter().GetResult());
        yield return ("cancelled_context_menu_returns_promptly_and_releases_late_commands", "Escape or dismissing a pending request returns before a slow extension finishes, and its eventual session is released without opening any menu.", () => CancelledMenu().GetAwaiter().GetResult());
        yield return ("superseded_context_menu_never_opens_after_loading", "A click or keypress that supersedes a pending right-click request prevents its completed shell menu from appearing later.", () => SupersededMenu().GetAwaiter().GetResult());
        yield return ("already_cancelled_context_menu_does_not_show_ready_commands", "Cancellation wins even when Windows' commands are already available, preventing a dismissed request from opening in a completion race.", () =>
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int opens = 0, releases = 0;
            ContextMenuPresentation.ShowAsync(Task.FromResult("ready commands"), () => true,
                _ => opens++, _ => releases++, cancelled.Token).GetAwaiter().GetResult();
            Check.Equal(0, opens);
            Check.Equal(1, releases);
        });
    }

    private static async Task SlowMenu()
    {
        var loading = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new List<string>();
        int opens = 0;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, () => true,
            commands =>
            {
                root.AddRange(["Open", "Copy"]);
                root.AddRange(commands);
                root.Add("Properties");
                opens++;
            }, _ => throw new Exception("current menu discarded its commands"));
        Check.Equal(0, opens, "partial menu opened before the extension finished");
        Check.Equal(0, root.Count);
        loading.SetResult(["Print", "WinRAR", "7-Zip"]);
        await showing;
        Check.Equal(1, opens);
        Check.Sequence(["Open", "Copy", "Print", "WinRAR", "7-Zip", "Properties"], root);
    }

    private static async Task CancelledMenu()
    {
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        int opens = 0, releases = 0;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, () => true,
            _ => opens++, _ => { Interlocked.Increment(ref releases); released.SetResult(); }, cancelled.Token);
        cancelled.Cancel();
        await showing.WaitAsync(TimeSpan.FromSeconds(2));
        Check.That(!loading.Task.IsCompleted, "cancellation waited for the shell extension");
        Check.Equal(0, opens);
        Check.Equal(0, releases);
        loading.SetResult("late session");
        await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(0, opens);
        Check.Equal(1, releases);
    }

    private static async Task SupersededMenu()
    {
        var loading = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        int opens = 0, releases = 0;
        Task showing = ContextMenuPresentation.ShowAsync(loading.Task, () => current,
            _ => opens++, _ => releases++);
        current = false;
        loading.SetResult("superseded session");
        await showing;
        Check.Equal(0, opens);
        Check.Equal(1, releases);
    }
}
