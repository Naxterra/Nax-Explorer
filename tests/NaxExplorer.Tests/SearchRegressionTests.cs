using NaxExplorer.Search;

namespace NaxExplorer.Tests;

internal static class SearchRegressionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("smart_filename_patterns_return_only_matching_files", "ZIP, image and question-mark filename patterns keep their literal meaning and never load the semantic model or return documents merely mentioning an extension.", () =>
        {
            using var store = NewStore();
            store.SaveNamesOnly([(@"C:\files\archive.ZIP", 1, 1), (@"C:\files\photo.png", 1, 1), (@"C:\files\scan.jpg", 1, 1), (@"C:\files\part1.bin", 1, 1)]);
            store.Save(@"C:\files\notes.txt", 1, 1, FileState.Indexed, [Chunk("zip png jpg archive notes")]);
            using var host = UnusedModel();
            var search = new HybridSearch(store, host);
            Check.Sequence([@"C:\files\archive.ZIP"], search.Search("*.zip", null, 100).Select(h => h.Path));
            Check.Sequence([@"C:\files\scan.jpg", @"C:\files\photo.png"], search.Search("*.jpg;*.png", null, 100).Select(h => h.Path));
            Check.Sequence([@"C:\files\part1.bin"], search.Search("part?.bin", null, 100).Select(h => h.Path));
            Check.Equal(0, search.Search("missing*.zip", null, 100).Count);
        });
        yield return ("quoted_filename_phrases_match_separators_and_exclude_guesses", "A quoted filename phrase matches spaces, underscores, periods and hyphens consistently, while an absent quoted phrase returns no semantic guesses.", () =>
        {
            using var store = NewStore();
            store.SaveNamesOnly([(@"C:\files\Example_Setup-v2.4.zip", 1, 1)]);
            store.Save(@"C:\files\unrelated.txt", 1, 1, FileState.Indexed, [Chunk("generic setup information")]);
            using var host = UnusedModel();
            var search = new HybridSearch(store, host);
            var hits = search.Search("\"Example Setup v2 4\"", null, 100);
            Check.Sequence([@"C:\files\Example_Setup-v2.4.zip"], hits.Select(h => h.Path));
            Check.That(hits[0].Kinds.HasFlag(MatchKind.Name), "normalized filename was not a name match");
            Check.Equal(0, search.Search("\"missing exact phrase\"", null, 100).Count);
        });
        yield return ("smart_names_accept_apostrophe_and_unicode_variants", "Straight and typographic apostrophes, omitted apostrophes, and composed or decomposed accents find the same filename.", () =>
        {
            using var store = NewStore();
            store.SaveNamesOnly([(@"C:\files\Baldur's Gate 3.url", 1, 1), (@"C:\files\München Übersicht.pdf", 1, 1)]);
            var search = new HybridSearch(store, null);
            foreach (string query in new[] { "Baldur’s Gate 3", "baldurs gate 3", "Baldur's Gate 3" })
                Check.Equal(@"C:\files\Baldur's Gate 3.url", search.Search(query, null, 10).Single().Path);
            foreach (string query in new[] { "München Übersicht", "Mu\u0308nchen U\u0308bersicht", "Munchen Ubersicht" })
                Check.Equal(@"C:\files\München Übersicht.pdf", search.Search(query, null, 10).Single().Path);
        });
        yield return ("exact_filename_stays_above_broader_name_with_content_matches", "A short exact filename stays ahead of a longer filename even when the latter also repeats the query in its contents.", () =>
        {
            using var store = NewStore();
            store.SaveNamesOnly([(@"C:\files\Budget.xlsx", 1, 1)]);
            store.Save(@"C:\files\Budget appendix.txt", 1, 1, FileState.Indexed, [Chunk("budget budget budget appendix")]);
            var hits = new HybridSearch(store, null).Search("budget", null, 100);
            Check.Equal(@"C:\files\Budget.xlsx", hits[0].Path);
        });
        yield return ("full_query_content_matches_do_not_turn_into_any_word_matches", "Searching several words without meaning returns documents containing all query words instead of adding files that contain only one common word.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\complete.txt", 1, 1, FileState.Indexed, [Chunk("invoice tax statement")]);
            store.Save(@"C:\files\partial.txt", 1, 1, FileState.Indexed, [Chunk("invoice invoice invoice")]);
            var hits = new HybridSearch(store, null).Search("invoice tax", null, 100, meaning: false);
            Check.Sequence([@"C:\files\complete.txt"], hits.Select(h => h.Path));
        });
        yield return ("full_text_file_limit_is_not_consumed_by_one_documents_chunks", "A long document cannot consume every full-text candidate slot with its own chunks and hide another matching document.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\long.txt", 1, 1, FileState.Indexed, Enumerable.Range(0, 420).Select(_ => Chunk("needle")).ToList());
            store.Save(@"C:\files\short.txt", 1, 1, FileState.Indexed, [Chunk("needle among other words in a short note")]);
            var hits = store.FullText(["needle"], false, 2, distinctFiles: true);
            Check.Equal(2, hits.Select(h => h.FileId).Distinct().Count());
            Check.Equal(2, new HybridSearch(store, null).Search("needle", null, 10, meaning: false).Count);
        });
        yield return ("empty_identifiers_cancelled_queries_and_questions_are_distinct", "Punctuation and missing identifiers return no guesses, cancellation is honored before reading the index, and a natural-language question retains semantic search.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\record.txt", 1, 1, FileState.Indexed, [Chunk("document information")]);
            using var unused = UnusedModel();
            var search = new HybridSearch(store, unused);
            foreach (string query in new[] { "!!!", "", "   ", "missing7429", "\"qzxvprmtn\"" }) Check.Equal(0, search.Search(query, null, 10).Count);
            Check.Equal(0, search.Search("record", null, 0).Count);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { search.Search("anything", null, 10, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check.That(stopped, "cancelled query was not stopped");
            using var host = new EmbedderHost(() => new ConstantEmbedder());
            Check.That(new HybridSearch(store, host).Search("Where are my invoices?", null, 10).Any(h => h.Kinds.HasFlag(MatchKind.Meaning)), "question mark was mistaken for a filename wildcard");
        });
        yield return ("semantic_document_limit_and_background_ignore_duplicate_chunks", "A long catalog's repeated chunks neither crowd another relevant document out of the semantic candidate budget nor distort the relevance background.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\catalog.txt", 1, 1, FileState.Indexed, Enumerable.Range(0, 1000).Select(_ => Chunk("catalog records")).ToList());
            store.Save(@"C:\files\guide.txt", 1, 1, FileState.Indexed, [Chunk("maintenance instructions")]);
            using var host = new EmbedderHost(() => new ConstantEmbedder());
            var hits = new HybridSearch(store, host).Search("technical recovery", null, 10);
            Check.Equal(2, hits.Count);
            Check.That(hits.All(h => h.Kinds.HasFlag(MatchKind.Meaning)), "semantic match lost");
        });
        yield return ("semantic_matches_use_filename_context_without_claiming_a_full_name_match", "A partial but relevant filename provides supporting evidence for a semantic result, while query filler words do not count as filename evidence.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\Employment certificate.txt", 1, 1, FileState.Indexed, [new StoredChunk("this certifies the holder works here", VectorMath.Quantize(AtSimilarity(.84f)))]);
            store.Save(@"C:\files\generic.txt", 1, 1, FileState.Indexed, [new StoredChunk("general administrative information", VectorMath.Quantize(AtSimilarity(.86f)))]);
            using var host = new EmbedderHost(() => new ConstantEmbedder());
            var hits = new HybridSearch(store, host).Search("proof of employment", null, 10);
            Check.Equal(@"C:\files\Employment certificate.txt", hits[0].Path);
            Check.That(hits[0].Kinds.HasFlag(MatchKind.Meaning) && !hits[0].Kinds.HasFlag(MatchKind.Name), "partial filename evidence mislabelled as a full name match");
            Check.Equal(0f, new SearchQuery("Where are my documents?").NameSupport("where are my notes.txt"));
        });
        yield return ("single_word_synonyms_remain_searchable_by_meaning", "A single descriptive word with no literal match still searches document meaning rather than silently disabling the model.", () =>
        {
            using var store = NewStore();
            store.Save(@"C:\files\contract.txt", 1, 1, FileState.Indexed, [Chunk("apartment lease agreement")]);
            using var host = new EmbedderHost(() => new ConstantEmbedder());
            var hits = new HybridSearch(store, host).Search("tenancy", null, 10);
            Check.Equal(@"C:\files\contract.txt", hits.Single().Path);
            Check.Equal(MatchKind.Meaning, hits[0].Kinds);
        });
        yield return ("semantic_background_cannot_reject_every_strong_match", "A collection of similarly relevant documents cannot raise the background threshold above its best document, while weak collections still respect the minimum similarity.", () =>
        {
            float[] scores = Enumerable.Range(0, 250).Select(i => i % 2 == 0 ? .88f : .90f).ToArray();
            Check.That(HybridSearch.MeaningFloor(scores, .90f) <= .90f, "background rejected the strongest result");
            Check.That(HybridSearch.MeaningFloor(Enumerable.Repeat(.70f, 250).ToArray(), .70f) >= HybridSearch.MinSimilarity, "weak background lowered the minimum similarity");
        });
    }

    private static IndexStore NewStore() => IndexStore.Open(Path.Combine(Fixtures.TempFolder("search-regressions"), "index.db"), "test-model");
    private static EmbedderHost UnusedModel() => new(() => throw new Exception("Literal query attempted to load the model."));
    private static StoredChunk Chunk(string text) => new(text, VectorMath.Quantize(ConstantEmbedder.Vector()));
    private static float[] AtSimilarity(float similarity) { var v = new float[EmbeddingModel.Dimensions]; v[0] = similarity; v[1] = MathF.Sqrt(1 - similarity * similarity); return v; }
    private sealed class ConstantEmbedder : IEmbedder
    {
        public int Dimensions => EmbeddingModel.Dimensions;
        public string Device => "test";
        public static float[] Vector() { var v = new float[EmbeddingModel.Dimensions]; v[0] = 1; return v; }
        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default) => texts.Select(_ => Vector()).ToArray();
        public void Dispose() { }
    }
}
