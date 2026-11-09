using System.Diagnostics;
using NaxExplorer.Core;
using NaxExplorer.Search;
using Windows.System;

namespace NaxExplorer.Tests;

internal static class TransitionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("viewer_shortcuts_take_navigation_before_scroll_controls", "Arrow keys, page keys and Escape select viewer commands even when a toolbar control has focus; the closed viewer cannot intercept file-list keys.", () =>
        {
            foreach (bool button in new[] { false, true })
            {
                Check.Equal(ViewerCommand.Previous, ViewerKeyboard.Command(VirtualKey.Left, true, button));
                Check.Equal(ViewerCommand.Next, ViewerKeyboard.Command(VirtualKey.Right, true, button));
                Check.Equal(ViewerCommand.Previous, ViewerKeyboard.Command(VirtualKey.PageUp, true, button));
                Check.Equal(ViewerCommand.Next, ViewerKeyboard.Command(VirtualKey.PageDown, true, button));
                Check.Equal(ViewerCommand.Close, ViewerKeyboard.Command(VirtualKey.Escape, true, button));
            }
            foreach (VirtualKey key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.Escape, VirtualKey.Enter, VirtualKey.Delete })
                Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(key, false));
        });

        yield return ("viewer_toolbar_activation_and_system_chords_keep_their_meaning", "Toolbar Enter and Space activate their focused command; Alt or Ctrl shortcuts and Tab are not mistaken for picture navigation.", () =>
        {
            Check.Equal(ViewerCommand.OpenExternal, ViewerKeyboard.Command(VirtualKey.Enter, true));
            Check.Equal(ViewerCommand.Next, ViewerKeyboard.Command(VirtualKey.Space, true));
            Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(VirtualKey.Enter, true, buttonFocused: true));
            Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(VirtualKey.Space, true, buttonFocused: true));
            foreach (VirtualKey key in new[] { VirtualKey.F4, VirtualKey.Left, VirtualKey.Right, VirtualKey.R, VirtualKey.Delete })
            {
                Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(key, true, control: true));
                Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(key, true, alt: true));
            }
            Check.Equal(ViewerCommand.None, ViewerKeyboard.Command(VirtualKey.Tab, true));
        });

        yield return ("viewer_reopen_waits_for_observed_zoom_and_offsets", "Reopening after custom zoom or a hidden DPI change retries an ignored reset until the actual zoom and pan settle; a user zoom cancels the pending reset.", () =>
        {
            var reset = new ViewResetState();
            reset.Request();
            Check.That(!reset.Observe(1.25, 1, 0, 0), "old zoom completed the reset");
            Check.That(!reset.Observe(1.25, 1, 240, 120), "an ignored request was not retried");
            Check.That(!reset.Observe(1, 1, 240, 120), "zoom settling hid stale pan offsets");
            Check.That(reset.Observe(1, 1, 0, 0) && !reset.Pending, "settled view did not finish the reset");
            reset.Request();
            reset.Cancel();
            Check.That(reset.Observe(1.5, 1, 200, 50) && !reset.Pending, "cancelled reset would override user zoom");
            reset.Request();
            Check.That(!reset.Observe(double.NaN, 1, 0, 0), "invalid layout completed the reset");
            Check.That(reset.Observe(0.5, 0.5, 0, 0), "large picture fit was not accepted");
        });

        yield return ("dpi_image_requests_reject_late_sizes_and_retry_cancelled_loads", "An icon requested at a new DPI is retried if the row left the screen before loading; a late old-size result cannot overwrite the current size, including a rapid round trip.", () =>
        {
            var request = new ImageRequestState();
            Check.That(request.Begin("20px", false) && request.Complete("20px"), "initial image was not delivered");
            Check.That(!request.Begin("20px", true), "same-size image was unnecessarily requested");
            Check.That(request.Begin("30px", true), "old-size image suppressed the DPI refresh");
            // No completion: the shell request was cancelled when the row left the screen.
            Check.That(request.Begin("30px", true), "cancelled new-size request was never retried");
            Check.That(!request.Complete("20px"), "late old-size image replaced the new request");
            Check.That(request.Complete("30px"), "new-size image was discarded");
            Check.That(request.Begin("20px", true), "return to original DPI was not requested");
            Check.That(!request.Begin("30px", true), "round-trip to already delivered size reloaded it");
            Check.That(!request.Complete("20px"), "late round-trip image replaced the current size");
        });

        yield return ("dpi_transition_preserves_image_pixels_zoom_and_scroll", "Moving through 100, 125, 150, 200 and back to 100 percent keeps actual-size and zoomed image dimensions in physical pixels, and scroll positions round-trip.", () =>
        {
            double offset = 180, oldScale = 1;
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0, 1.0 })
            {
                double width = DpiMetrics.LogicalPixels(640, scale), height = DpiMetrics.LogicalPixels(360, scale);
                Check.That(Math.Abs(width * scale - 640) < 0.00001, "actual width changed physical pixels");
                Check.That(Math.Abs(height * scale * 1.25 - 450) < 0.00001, "125 percent zoom changed physical pixels");
                offset = DpiMetrics.RescaleOffset(offset, oldScale, scale);
                Check.That(Math.Abs(offset * scale - 180) < 0.00001, "pan position moved in physical pixels");
                oldScale = scale;
            }
            Check.Equal(640.0, DpiMetrics.LogicalPixels(640, double.NaN));
            Check.Equal(640.0, DpiMetrics.LogicalPixels(640, 0));
        });

        yield return ("battery_policy_distinguishes_ac_offline_unknown_and_disabled", "Windows AC readings 0/1/255 and unavailable readings honor the battery option; fullscreen retains precedence and disabled probes are not called.", () =>
        {
            foreach (byte? ac in new byte?[] { 0, 1, 255, null })
                Check.Equal(ac == 0 ? SystemActivity.Reason.Battery : SystemActivity.Reason.None,
                    SystemActivity.EvaluatePause(false, true, () => throw new Exception("fullscreen probe disabled"), () => false, () => ac));
            Check.Equal(SystemActivity.Reason.None, SystemActivity.EvaluatePause(false, false,
                () => throw new Exception("fullscreen disabled"), () => false, () => throw new Exception("battery disabled")));
            Check.Equal(SystemActivity.Reason.FullScreen, SystemActivity.EvaluatePause(true, true, () => 3, () => false,
                () => throw new Exception("fullscreen should take precedence")));
        });

        yield return ("battery_transition_is_detected_while_indexer_is_idle", "Without file events or an explicit nudge, the real worker detects AC to battery and battery to AC promptly while idle.", () =>
        {
            using var store = Store();
            using var host = new EmbedderHost(() => new FakeEmbedder());
            int ac = 1;
            using var indexer = new Indexer(store, host, (full, battery) => Reason(full, battery, Volatile.Read(ref ac))) { PauseInFullScreen = false };
            indexer.Start([Fixtures.TempFolder("battery-idle")]);
            Until(() => indexer.Status.Phase == IndexerPhase.Idle, "initial idle");
            Volatile.Write(ref ac, 0);
            Until(() => indexer.Status.Phase == IndexerPhase.Paused && indexer.Status.Detail == "battery", "idle battery pause", 3);
            Check.That(!host.IsLoaded, "idle power transition loaded the model");
            Volatile.Write(ref ac, 1);
            Until(() => indexer.Status.Phase == IndexerPhase.Idle, "idle AC resume", 3);
        });

        yield return ("battery_pause_blocks_scans_and_watchers_until_ac_or_option_override", "A battery start does not scan or load the model; disabling the option indexes normally, and re-enabling it defers watcher changes until AC resumes.", () =>
        {
            string root = Fixtures.TempFolder("battery-files");
            Fixtures.Write(root, "first.txt", "first document");
            Fixtures.Write(root, "archive.zip", "name only");
            using var store = Store();
            using var host = new EmbedderHost(() => new FakeEmbedder());
            int ac = 0;
            using var indexer = new Indexer(store, host, (full, battery) => Reason(full, battery, Volatile.Read(ref ac))) { PauseInFullScreen = false };
            indexer.Start([root]);
            Until(() => indexer.Status.Phase == IndexerPhase.Paused, "battery start");
            Check.Equal(0, store.Counts().Files);
            Check.That(!host.IsLoaded, "battery start loaded the model");
            indexer.PauseOnBattery = false;
            indexer.Nudge();
            Until(() => store.Counts().Files == 2 && indexer.Status.Phase == IndexerPhase.Idle, "option override");
            indexer.PauseOnBattery = true;
            indexer.Nudge();
            Until(() => indexer.Status.Phase == IndexerPhase.Paused, "option restored");
            string next = Fixtures.Write(root, "next.txt", "queued on battery");
            Thread.Sleep(3300);
            Check.That(store.Find(next) is null, "watcher indexed a file on battery");
            Volatile.Write(ref ac, 1);
            Until(() => store.Find(next) is not null && indexer.Status.Phase == IndexerPhase.Idle, "watcher after AC", 5);
        });

        yield return ("battery_change_during_embedding_defers_database_writes", "If battery power begins during an already-running model call, its result is not written to the index until AC returns.", () =>
        {
            string root = Fixtures.TempFolder("battery-embedding");
            Fixtures.Write(root, "document.txt", "database content");
            using var store = Store();
            using var embedder = new BlockingEmbedder();
            using var host = new EmbedderHost(() => embedder);
            int ac = 1;
            using var indexer = new Indexer(store, host, (full, battery) => Reason(full, battery, Volatile.Read(ref ac))) { PauseInFullScreen = false };
            try
            {
                indexer.Start([root]);
                Check.That(embedder.Entered.Wait(TimeSpan.FromSeconds(5)), "model did not start");
                Volatile.Write(ref ac, 0);
                Thread.Sleep(350); // Let the normal activity cache expire while the native call is outstanding.
                embedder.Release.Set();
                Until(() => indexer.Status.Phase == IndexerPhase.Paused, "pause after inference", 3);
                Check.Equal(0, store.Counts().Documents, "in-flight inference wrote on battery");
                Volatile.Write(ref ac, 1);
                Until(() => store.Counts().Documents == 1, "inference saved after AC", 3);
            }
            finally { embedder.Release.Set(); }
        });
    }

    private static SystemActivity.Reason Reason(bool full, bool battery, int ac) =>
        SystemActivity.EvaluatePause(full, battery, () => null, () => false, () => (byte)ac);

    private static IndexStore Store() => IndexStore.Open(Path.Combine(Fixtures.TempFolder("transition-index"), "index.db"), "test-model");

    private static void Until(Func<bool> condition, string scenario, int seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition()) return;
            Thread.Sleep(25);
        }
        throw new Exception("timed out: " + scenario);
    }

    private sealed class BlockingEmbedder : IEmbedder
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int Dimensions => EmbeddingModel.Dimensions;
        public string Device => "test";
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default)
        {
            Entered.Set();
            Release.Wait();
            return new FakeEmbedder().Embed(texts);
        }
        public void Dispose() => Release.Set();
    }
}
