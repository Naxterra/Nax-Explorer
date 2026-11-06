using System.Diagnostics;
using NaxExplorer.Search;
using NaxExplorer.Core;
using NaxExplorer.Services;

namespace NaxExplorer.Tests;

internal static class RegressionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("replace_only_applies_to_conflicts_shown_to_the_user", "Choosing Replace for one conflict cannot silently authorize replacing another file that appears after the question.", () =>
        {
            string root = Fixtures.TempFolder("late-conflict");
            string first = Fixtures.Write(root, @"source\first.txt", "new first");
            string second = Fixtures.Write(root, @"source\second.txt", "new second");
            string existing = Fixtures.Write(root, @"target\first.txt", "old first");
            string target = Path.GetDirectoryName(existing)!;
            TransferConflicts shown = TransferPlan.FindConflicts([first, second], target);
            Fixtures.Write(target, "second.txt", "arrived after the question");
            var batches = TransferPlan.Plan([first, second], target, false, ConflictChoice.Replace, shown.Items);
            Check.That((batches.Single(b => b.Sources.Contains(first)).Flags & OperationFlags.NoConfirmation) != 0, "confirmed replacement lost its answer");
            Check.That((batches.Single(b => b.Sources.Contains(second)).Flags & OperationFlags.NoConfirmation) == 0, "unseen conflict was silently authorized");
        });

        yield return ("cut_paste_preserves_failed_skipped_and_newer_clipboard_items", "Only a successful move of every source may clear its original clipboard version; failures, skipped files, partly merged folders and a newer Copy are retained.", () =>
        {
            string root = Fixtures.TempFolder("cut-paste");
            string moved = Fixtures.Write(root, "moved.txt", "move me");
            string skipped = Fixtures.Write(root, "skipped.txt", "keep me");
            var completion = new CutPasteCompletion([moved, skipped], 42);
            File.Move(moved, Path.Combine(root, "destination.txt"));
            Check.That(!completion.AllMoved(true), "partly skipped move cleared the clipboard");
            File.Move(skipped, Path.Combine(root, "destination2.txt"));
            Check.That(!completion.AllMoved(false), "failed or cancelled operation cleared the clipboard");
            Check.That(completion.AllMoved(true), "completed move did not release its cut selection");
            Check.That(completion.OwnsClipboard(42), "original clipboard version not recognized");
            Check.That(!completion.OwnsClipboard(43), "a newer clipboard version would be erased");
            Check.That(!new CutPasteCompletion([moved], 0).OwnsClipboard(0), "unavailable clipboard version treated as ownership");
            string folder = Path.Combine(root, "merged");
            Directory.CreateDirectory(folder);
            Fixtures.Write(folder, "skipped.txt", "keep me");
            Check.That(!new CutPasteCompletion([folder], 42).AllMoved(true), "partly merged folder lost its cut selection");
        });

        yield return ("scoped_words_are_limited_after_filtering_the_folder", "More than 400 better matches outside the selected folder cannot hide its matching document; SQL wildcard characters in folder names are literal.", () =>
        {
            using var store = NewStore();
            for (int i = 0; i < 450; i++) store.Save($@"C:\outside\{i}.txt", 1, 1, FileState.Indexed, Pieces("needle"));
            const string inside = @"C:\docs_100%\nested\notes.txt";
            store.Save(inside, 1, 1, FileState.Indexed, Pieces("needle " + string.Join(' ', Enumerable.Repeat("padding", 100))));
            store.Save(@"C:\docsX100more\other.txt", 1, 1, FileState.Indexed, Pieces("needle"));
            var hits = new HybridSearch(store, null).Search("needle", @"C:\docs_100%", 10, meaning: false);
            Check.Sequence([inside], hits.Select(h => h.Path));
        });

        yield return ("scoped_words_match_non_ascii_folder_names_case_insensitively", "Folder-scoped full-text search uses Windows-style ordinal case-insensitive comparison for accented folder names too.", () =>
        {
            using var store = NewStore();
            const string inside = @"C:\BÜRO\Überblick\notes.txt";
            store.Save(inside, 1, 1, FileState.Indexed, Pieces("needle"));
            store.Save(@"C:\BÜRO2\other.txt", 1, 1, FileState.Indexed, Pieces("needle"));
            var hits = new HybridSearch(store, null).Search("needle", @"c:\büro\überblick", 10, meaning: false);
            Check.Sequence([inside], hits.Select(h => h.Path));
        });

        yield return ("stopping_search_retains_resources_until_active_queries_finish", "Stopping during a semantic query lets that query finish its snippet read before releasing its database and model.", () =>
        {
            var store = NewStore();
            store.Save(@"C:\inside\notes.txt", 1, 1, FileState.Indexed,
                [new StoredChunk("related wording", VectorMath.Quantize(new FakeEmbedder().Embed(["topic"])[0]))]);
            using var embedder = new BlockingEmbedder();
            var host = new EmbedderHost(() => embedder);
            using var service = new SearchService();
            void Set(string field, object value) => typeof(SearchService).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, value);
            Set("_store", store);
            Set("_host", host);
            Set("_search", new HybridSearch(store, host));
            Task<List<SmartHit>> query = service.SearchAsync("topic", null, default);
            try
            {
                Check.That(embedder.Entered.Wait(TimeSpan.FromSeconds(10)), "query did not start embedding");
                service.Stop();
                Check.That(!service.IsRunning, "stopped service still accepts queries");
                Check.That(!service.ShutdownCompletion.IsCompleted, "index deletion could race the active query");
                embedder.Release.Set();
                Check.That(query.Wait(TimeSpan.FromSeconds(5)), "query did not finish");
                Check.Equal("related wording", query.Result.Single().Snippet, "active query lost its database");
                Check.That(service.ShutdownCompletion.Wait(TimeSpan.FromSeconds(5)), "retired resources were not released");
                WaitFor(() => !host.IsLoaded, "deferred model disposal");
            }
            finally { embedder.Release.Set(); }
        });

        yield return ("scoped_meaning_is_ranked_inside_the_folder", "More than 800 semantic matches elsewhere cannot hide a related document in the selected folder.", () =>
        {
            using var store = NewStore();
            for (int i = 0; i < 810; i++) store.Save($@"C:\outside\{i}.txt", 1, 1, FileState.Indexed, Pieces("topic"));
            store.Save(@"C:\inside\notes.txt", 1, 1, FileState.Indexed, Pieces("topic"));
            using var host = new EmbedderHost(() => new FakeEmbedder());
            var hits = new HybridSearch(store, host).Search("topic", @"C:\inside", 10);
            Check.That(hits.Single().Kinds.HasFlag(MatchKind.Meaning), "scoped semantic match was dropped before scope filtering");
        });

        yield return ("changing_roots_discards_queued_and_inflight_documents", "Removing a root during embedding prevents that batch and its remaining queue from writing documents back; adding it again still indexes normally.", () =>
        {
            string root = Fixtures.TempFolder("removed-root");
            for (int i = 0; i < 52; i++) Fixtures.Write(root, $"note{i}.txt", "document content");
            using var store = NewStore();
            using var embedder = new BlockingEmbedder();
            using var host = new EmbedderHost(() => embedder);
            using var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false };
            try
            {
                indexer.Start([root]);
                Check.That(embedder.Entered.Wait(TimeSpan.FromSeconds(10)), "embedding did not start");
                indexer.SetRoots([]);
                embedder.Release.Set();
                WaitFor(() => indexer.Status.Phase == IndexerPhase.Idle, "removed-root scan");
                Check.Equal(0, store.Counts().Files, "removed documents reappeared");
                indexer.SetRoots([root]);
                WaitFor(() => store.Counts().Documents == 52, "re-added root");
            }
            finally { embedder.Release.Set(); }
        });

        yield return ("queued_documents_recheck_file_exclusions_before_reading_and_saving", "Files made hidden while embedding is blocked stay out of the index, including queued files; making them visible and rescanning indexes them normally.", () =>
        {
            string root = Fixtures.TempFolder("changed-exclusions");
            string[] files = Enumerable.Range(0, 52).Select(i => Fixtures.Write(root, $"note{i}.txt", "document content")).ToArray();
            using var store = NewStore();
            using var embedder = new BlockingEmbedder();
            using var host = new EmbedderHost(() => embedder);
            using var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false };
            try
            {
                indexer.Start([root]);
                Check.That(embedder.Entered.Wait(TimeSpan.FromSeconds(10)), "embedding did not start");
                foreach (string file in files) File.SetAttributes(file, FileAttributes.Hidden);
                embedder.Release.Set();
                WaitFor(() => indexer.Status.Phase == IndexerPhase.Idle, "excluded queue draining");
                Check.Equal(0, store.Counts().Files, "newly excluded files entered the index");
                foreach (string file in files) File.SetAttributes(file, FileAttributes.Normal);
                indexer.Rescan();
                WaitFor(() => store.Counts().Documents == 52, "visible files after rescan");
            }
            finally { embedder.Release.Set(); }
        });

        yield return ("watchers_respect_excluded_ancestors_and_renamed_folders", "Created files and whole folders moved into .git stay out of the index, while their former indexed paths are removed.", () =>
        {
            string root = Fixtures.TempFolder("excluded-watcher");
            string old = Fixtures.Write(root, @"visible\note.txt", "initial document");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            using var store = NewStore();
            using var host = new EmbedderHost(() => new FakeEmbedder());
            using var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false };
            indexer.Start([root]);
            WaitFor(() => store.Find(old) is not null, "initial scan");
            Fixtures.Write(root, @".git\private.txt", "excluded private document");
            Directory.Move(Path.Combine(root, "visible"), Path.Combine(root, @".git\moved"));
            string sentinel = Fixtures.Write(root, "sentinel.txt", "allowed change");
            WaitFor(() => store.Find(sentinel) is not null && store.Find(old) is null && indexer.Status.Phase == IndexerPhase.Idle, "watcher processing");
            Check.Sequence([sentinel], store.AllPaths().Select(p => p.Path));
        });

        yield return ("shutdown_with_slow_embedding_does_not_crash_after_disposal", "A non-cooperative model outlasting the shutdown timeout cannot write to a disposed database or crash the process.", () =>
        {
            if (Environment.GetCommandLineArgs().Contains("--shutdown-child"))
            {
                string root = Fixtures.TempFolder("shutdown");
                Fixtures.Write(root, "note.txt", "slow embedding");
                var store = NewStore();
                using var embedder = new BlockingEmbedder();
                var host = new EmbedderHost(() => embedder);
                var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false };
                indexer.Start([root]);
                Check.That(embedder.Entered.Wait(TimeSpan.FromSeconds(10)), "embedding did not start");
                indexer.Dispose();
                host.Dispose();
                store.Dispose();
                embedder.Release.Set();
                Check.That(indexer.Completion.Wait(TimeSpan.FromSeconds(5)), "index worker did not exit");
                return;
            }
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("shutdown_with_slow_embedding_does_not_crash_after_disposal");
            start.ArgumentList.Add("--shutdown-child");
            using var child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000)) { child.Kill(); throw new Exception("shutdown child timed out"); }
            Check.That(child.ExitCode == 0, $"shutdown child exited {child.ExitCode}: {stdout.Result} {stderr.Result}");
        });
    }

    private static IndexStore NewStore() => IndexStore.Open(Path.Combine(Fixtures.TempFolder("regression-index"), "index.db"), "test-model");
    private static List<StoredChunk> Pieces(string text) => [new(text, VectorMath.Quantize(new FakeEmbedder().Embed([text])[0]))];
    private static void WaitFor(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
        throw new Exception("timed out waiting for " + what);
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
            Release.Wait(); // Deliberately ignores cancellation, like a stalled native inference call.
            return new FakeEmbedder().Embed(texts);
        }
        public void Dispose() => Release.Set();
    }
}
