using System.Security.Cryptography;
using NaxExplorer.Search;

namespace NaxExplorer.Tests;

/// <summary>
/// A stand-in for the model: words are hashed into the vector, so texts that share words get similar vectors. It lets
/// the index, the indexer and the ranking be tested without the real model.
/// </summary>
internal sealed class FakeEmbedder : IEmbedder
{
    public int Dimensions => EmbeddingModel.Dimensions;

    public string Device => "test";

    public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default)
    {
        return texts.Select(text =>
        {
            var vector = new float[Dimensions];
            foreach (string word in text.ToLowerInvariant().Split([' ', '\n', ':', '.', ',', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word is "query" or "passage") continue;
                vector[(int)((uint)word.GetHashCode(StringComparison.Ordinal) % Dimensions)] += 1;
            }
            vector[0] += 0.01f;
            VectorMath.Normalize(vector);
            return vector;
        }).ToArray();
    }

    public void Dispose()
    {
    }
}

internal static class IndexTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("single_name_query_keeps_literal_hits_without_semantic_noise", "When one search term matches an indexed filename, that file and literal text matches are returned without adding unrelated high-similarity documents or loading the model.", () =>
        {
            using IndexStore store = NewStore(out _);
            const string named = @"C:\games\Birthright-Alternative-ISO-Version.zip";
            store.SaveNamesOnly([(named, 1, 1)]);
            store.Save(@"C:\games\game-notes.txt", 1, 1, FileState.Indexed, Pieces("Birthright is a fantasy strategy game."));
            store.Save(@"C:\games\unrelated.txt", 1, 1, FileState.Indexed, Pieces("unrelated permit information"));
            bool loaded = false;
            using var host = new EmbedderHost(() => { loaded = true; return new QueryEchoEmbedder("unrelated permit information"); });
            var hits = new HybridSearch(store, host).Search("Birthright", null, 100);
            Check.Sequence([named, @"C:\games\game-notes.txt"], hits.Select(h => h.Path));
            Check.That(!loaded, "a known filename lookup unnecessarily loaded the semantic model");
        });
        yield return ("name_intent_is_scoped_and_multiword_queries_keep_meaning", "A name outside the selected folder does not suppress that folder's semantic results, and descriptive multiword queries still find paraphrases alongside named files.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.SaveNamesOnly([(@"C:\elsewhere\Birthright.zip", 1, 1), (@"C:\docs\oracle_upgrade.txt", 1, 1)]);
            store.Save(@"C:\docs\runbook.txt", 1, 1, FileState.Indexed, Pieces("database version migration instructions"));
            using var host = new EmbedderHost(() => new QueryEchoEmbedder("database version migration instructions"));
            var search = new HybridSearch(store, host);
            Check.That(search.Search("Birthright", @"C:\docs", 100).Any(h => h.Kinds.HasFlag(MatchKind.Meaning)), "a filename outside the scope suppressed meaning search");
            var hits = search.Search("oracle upgrade", @"C:\docs", 100);
            Check.Equal(@"C:\docs\oracle_upgrade.txt", hits[0].Path);
            Check.That(hits.Any(h => h.Path.EndsWith("runbook.txt") && h.Kinds.HasFlag(MatchKind.Meaning)), "descriptive search lost its paraphrase match");
        });
        yield return ("chunker_keeps_short_text_whole_and_overlaps_long_text", "A short text stays one piece; a long text becomes pieces near the target size that share some text at each cut.", () =>
        {
            Check.Equal(1, Chunker.Split("Short note.\n\nSecond paragraph.").Count, "short text pieces");
            string sentence = "The upgrade runbook lists every step in order. ";
            string longText = string.Concat(Enumerable.Repeat(sentence, 200));
            List<string> pieces = Chunker.Split(longText);
            Check.That(pieces.Count is > 5 and < 15, $"unexpected piece count {pieces.Count}");
            Check.That(pieces.All(p => p.Length <= Chunker.TargetChars + Chunker.TargetChars / 4), "a piece is too long");
            string endOfFirst = pieces[0][^40..];
            Check.That(pieces[1].Contains(endOfFirst[..20]), "pieces do not overlap");
        });

        yield return ("full_text_query_quotes_words_so_symbols_are_not_operators", "Words become quoted FTS5 terms, inner quotes are doubled and words without letters are dropped.", () =>
        {
            Check.Equal("\"c++\"* AND \"say \"\"hi\"\"\"", IndexStore.BuildMatch(["c++", "say \"hi\"", "-"], anyWord: false));
            Check.Equal("\"a\"* OR \"b\"*", IndexStore.BuildMatch(["a", "b"], anyWord: true));
            Check.Equal(null, IndexStore.BuildMatch(["--", "  "], anyWord: false));
        });

        yield return ("saving_a_file_again_replaces_its_old_pieces", "When a document is indexed again, its old text can no longer be found.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\docs\a.txt", 1, 1, FileState.Indexed, Pieces("old wording about tulips"));
            store.Save(@"C:\docs\a.txt", 2, 2, FileState.Indexed, Pieces("new wording about roses"));
            Check.Equal(0, store.FullText(["tulips"], false, 10).Count, "old text hits");
            Check.Equal(1, store.FullText(["roses"], false, 10).Count, "new text hits");
            Check.Equal(1, store.Counts().Chunks, "pieces");
        });

        yield return ("removing_a_folder_spares_a_folder_with_a_longer_name", "Removing C:\\docs from the index keeps C:\\docs2.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\docs\a.txt", 1, 1, FileState.Indexed, Pieces("alpha"));
            store.Save(@"C:\docs\sub\b.txt", 1, 1, FileState.Indexed, Pieces("beta"));
            store.Save(@"C:\docs2\c.txt", 1, 1, FileState.Indexed, Pieces("gamma"));
            store.RemoveUnder(@"C:\DOCS");
            Check.Sequence([@"C:\docs2\c.txt"], store.AllPaths().Select(p => p.Path));
            Check.Equal(1, store.Counts().Chunks, "pieces left");
        });

        yield return ("damaged_index_is_moved_aside_and_rebuilt", "An index file that is not a database is kept aside as index.damaged-*.db and a new, empty index starts.", () =>
        {
            string folder = Fixtures.TempFolder("index");
            string path = Path.Combine(folder, "index.db");
            File.WriteAllText(path, "this is not a database, just text that is long enough to look like a header");
            using (IndexStore store = IndexStore.Open(path, "model-a"))
            {
                Check.Equal(0, store.Counts().Files, "files");
            }
            Check.Equal(1, Directory.GetFiles(folder, "index.damaged-*.db").Length, "damaged copies");
        });

        yield return ("changing_the_model_empties_the_index", "Vectors of an old model are never compared with a new one: a different model id clears the index.", () =>
        {
            string folder = Fixtures.TempFolder("index");
            string path = Path.Combine(folder, "index.db");
            using (IndexStore store = IndexStore.Open(path, "model-a")) store.Save(@"C:\a.txt", 1, 1, FileState.Indexed, Pieces("alpha"));
            using (IndexStore store = IndexStore.Open(path, "model-a")) Check.Equal(1, store.Counts().Files, "same model keeps files");
            using (IndexStore store = IndexStore.Open(path, "model-b")) Check.Equal(0, store.Counts().Files, "new model clears files");
        });

        yield return ("smart_search_puts_name_matches_first", "A file named after the query ranks above documents that only mention the words.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\d\notes.txt", 1, 1, FileState.Indexed, Pieces("we discussed the oracle upgrade and the oracle upgrade window"));
            store.SaveNamesOnly([(@"C:\d\Oracle_Upgrade_Checklist.xlsx", 1, 1)]);
            using var host = new EmbedderHost(() => new FakeEmbedder());
            List<SmartHit> hits = new HybridSearch(store, host).Search("oracle upgrade", null, 10);
            Check.Equal(@"C:\d\Oracle_Upgrade_Checklist.xlsx", hits[0].Path, "first hit");
            Check.That(hits[0].Kinds.HasFlag(MatchKind.Name), "name flag");
            Check.That(hits.Any(h => h.Path.EndsWith("notes.txt") && h.Kinds.HasFlag(MatchKind.Words)), "word match missing");
        });

        yield return ("smart_search_stays_inside_the_current_folder", "Searching in C:\\a returns nothing from C:\\b.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\a\x.txt", 1, 1, FileState.Indexed, Pieces("kubernetes cluster notes"));
            store.Save(@"C:\b\y.txt", 1, 1, FileState.Indexed, Pieces("kubernetes cluster notes"));
            using var host = new EmbedderHost(() => new FakeEmbedder());
            List<SmartHit> hits = new HybridSearch(store, host).Search("kubernetes", @"C:\a", 10);
            Check.Sequence([@"C:\a\x.txt"], hits.Select(h => h.Path));
        });

        yield return ("smart_search_finds_words_without_the_model", "Without a loaded model, names and exact words are still found.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\a\x.txt", 1, 1, FileState.Indexed, Pieces("tablespace growth report"));
            List<SmartHit> hits = new HybridSearch(store, null).Search("tablespace", null, 10);
            Check.Sequence([@"C:\a\x.txt"], hits.Select(h => h.Path));
            Check.That(hits[0].Snippet!.Contains(IndexStore.MarkStart + "tablespace" + IndexStore.MarkEnd), $"snippet not marked: {hits[0].Snippet}");
        });

        yield return ("busy_state_pauses_indexing_only_for_a_window_covering_the_monitor", "Exclusive full screen and presentation mode always pause indexing; the busy state pauses it only when the front window covers its monitor.", () =>
        {
            Check.That(SystemActivity.FullScreenPause(3, () => false), "exclusive full screen did not pause");
            Check.That(SystemActivity.FullScreenPause(4, () => false), "presentation mode did not pause");
            Check.That(SystemActivity.FullScreenPause(2, () => true), "a borderless full-screen game did not pause");
            Check.That(!SystemActivity.FullScreenPause(2, () => false), "busy without a full-screen window paused");
            Check.That(!SystemActivity.FullScreenPause(5, () => true), "the normal state paused");
        });

        yield return ("quick_search_answers_without_loading_the_model", "The first, quick pass of a smart search finds names and words without ever loading the model.", () =>
        {
            using IndexStore store = NewStore(out _);
            store.Save(@"C:\a\x.txt", 1, 1, FileState.Indexed, Pieces("tablespace growth report"));
            bool loaded = false;
            using var host = new EmbedderHost(() =>
            {
                loaded = true;
                return new FakeEmbedder();
            });
            List<SmartHit> hits = new HybridSearch(store, host).Search("tablespace", null, 10, meaning: false);
            Check.Sequence([@"C:\a\x.txt"], hits.Select(h => h.Path));
            Check.That(!loaded && !host.IsLoaded, "the model was loaded");
        });

        yield return ("meaning_cut_off_rises_above_the_background_of_a_large_index", "In a large index the cut-off for meaning-only matches lies well above the typical similarity, and never below the fixed floor.", () =>
        {
            var scores = new float[1000];
            var random = new Random(7);
            for (int i = 0; i < scores.Length; i++) scores[i] = 0.74f + (float)(random.NextDouble() - 0.5) * 0.04f;
            float floor = HybridSearch.MeaningFloor(scores, 0.80f);
            Check.That(floor > 0.765f && floor < 0.775f, $"unexpected floor {floor:F3}");
            Check.Equal(HybridSearch.MinSimilarity, HybridSearch.MeaningFloor(scores.AsSpan(0, 10), 0.70f), "small index floor");
        });

        yield return ("indexer_follows_new_changed_and_deleted_files", "The indexer reads documents in an indexed folder, records other files by name, picks up a new file and forgets a deleted one.", () =>
        {
            string folder = Fixtures.TempFolder("indexer");
            Fixtures.Write(folder, "runbook.txt", "steps for the database upgrade weekend");
            Fixtures.Docx(folder, "minutes.docx", "Minutes of the planning meeting");
            File.WriteAllBytes(Path.Combine(folder, "oracle upgrade media.zip"), [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            using IndexStore store = NewStore(out _);
            using var host = new EmbedderHost(() => new FakeEmbedder());
            using var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false };
            indexer.Start([folder]);
            WaitFor(() => store.Counts().Documents == 2 && store.Counts().Files == 3, "first scan");
            var search = new HybridSearch(store, host);
            Check.That(search.Search("planning meeting", null, 10).Any(h => h.Path.EndsWith("minutes.docx")), "docx not searchable");
            Check.That(search.Search("oracle upgrade", null, 10).Any(h => h.Path.EndsWith(".zip") && h.Kinds.HasFlag(MatchKind.Name)), "zip name not found");
            Fixtures.Write(folder, "later.md", "a note about failover testing");
            WaitFor(() => search.Search("failover", null, 10).Count == 1, "new file");
            File.Delete(Path.Combine(folder, "runbook.txt"));
            WaitFor(() => store.Counts().Files == 3 && search.Search("weekend", null, 10).Count == 0, "deleted file");
        });

        yield return ("paused_indexer_does_not_scan_or_update_the_index", "Starting while paused leaves both file names and document contents unindexed until indexing resumes.", () =>
        {
            string folder = Fixtures.TempFolder("paused-indexer");
            Fixtures.Write(folder, "notes.txt", "database notes");
            Fixtures.Write(folder, "archive.zip", "name only");
            using IndexStore store = NewStore(out _);
            using var host = new EmbedderHost(() => new FakeEmbedder());
            using var indexer = new Indexer(store, host) { UserPaused = true, PauseInFullScreen = false, PauseOnBattery = false };
            indexer.Start([folder]);
            WaitFor(() => indexer.Status.Phase == IndexerPhase.Paused, "paused status");
            Check.Equal(0, store.Counts().Files, "files scanned while paused");
            Check.That(!host.IsLoaded, "model loaded while paused");
            indexer.UserPaused = false;
            indexer.Nudge();
            WaitFor(() => store.Counts().Files == 2 && store.Counts().Documents == 1, "resume after pause");
        });

        yield return ("disposed_model_host_releases_the_model_after_its_last_lease", "Stopping AI search during an active lease retains the model until the lease ends, then disposes it exactly once.", () =>
        {
            var embedder = new DisposalEmbedder();
            using var host = new EmbedderHost(() => embedder);
            EmbedderHost.Lease first = host.Acquire(), last = host.Acquire();
            host.Dispose();
            Check.Equal(0, embedder.Disposals, "disposed an active model");
            first.Dispose();
            Check.Equal(0, embedder.Disposals);
            last.Dispose();
            last.Dispose();
            Check.Equal(1, embedder.Disposals, "model retained after final lease");
            Check.That(!host.IsLoaded, "disposed model remains loaded");
        });

        yield return ("indexing_never_changes_the_users_files", "After indexing, every file has the same bytes, size and modification time as before.", () =>
        {
            string folder = Fixtures.TempFolder("untouched");
            string text = Fixtures.Write(folder, "a.txt", "plain text content");
            string docx = Fixtures.Docx(folder, "b.docx", "document content");
            var before = Snapshot(text, docx);
            using IndexStore store = NewStore(out _);
            using var host = new EmbedderHost(() => new FakeEmbedder());
            using (var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false })
            {
                indexer.Start([folder]);
                WaitFor(() => store.Counts().Documents == 2, "indexing");
            }
            Check.Sequence(before, Snapshot(text, docx), "file state");
        });
    }

    private sealed class DisposalEmbedder : IEmbedder
    {
        public int Disposals { get; private set; }
        public int Dimensions => 384;
        public string Device => "test";
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }

    private sealed class QueryEchoEmbedder(string passage) : IEmbedder
    {
        public int Dimensions => EmbeddingModel.Dimensions;
        public string Device => "test";
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default) => texts.Select(_ => new FakeEmbedder().Embed(["passage: " + passage])[0]).ToArray();
        public void Dispose() { }
    }

    private static IndexStore NewStore(out string folder)
    {
        folder = Fixtures.TempFolder("index");
        return IndexStore.Open(Path.Combine(folder, "index.db"), "test-model");
    }

    private static List<StoredChunk> Pieces(params string[] texts)
    {
        var embedder = new FakeEmbedder();
        return texts.Select(t => new StoredChunk(t, VectorMath.Quantize(embedder.Embed(["passage: " + t])[0]))).ToList();
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        throw new Exception($"timed out waiting for {what}");
    }

    private static List<string> Snapshot(params string[] paths) =>
        paths.Select(p => $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}").ToList();
}
