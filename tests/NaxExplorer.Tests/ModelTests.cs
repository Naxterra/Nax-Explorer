using System.Diagnostics;
using NaxExplorer.Search;

namespace NaxExplorer.Tests;

/// <summary>Tests with the real model. They are skipped, and say so, when the model has not been downloaded.</summary>
internal static class ModelTests
{
    private static readonly Lazy<OnnxEmbedder> Gpu = new(() => new OnnxEmbedder(EmbeddingModel.ModelPath, EmbeddingModel.TokenizerPath, preferGpu: true));

    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("tokenizer_gives_the_model_vocabulary_ids", "\"Hello world\" becomes the XLM-RoBERTa ids 0, 35378, 8999, 2.", () =>
        {
            RequireModel();
            var tokenizer = new XlmrTokenizer(EmbeddingModel.TokenizerPath);
            Check.Sequence([0L, 35378L, 8999L, 2L], tokenizer.Encode("Hello world", 512));
        });

        yield return ("model_runs_on_the_graphics_card", "With the GPU preferred, the model runs through DirectML and returns unit-length vectors of 384 numbers.", () =>
        {
            RequireModel();
            float[][] vectors = Gpu.Value.Embed(["query: test"]);
            Check.Equal("GPU (DirectML)", Gpu.Value.Device, "device");
            Check.Equal(384, vectors[0].Length, "dimensions");
            Check.That(Math.Abs(VectorMath.Dot(vectors[0], vectors[0]) - 1) < 1e-3, "not unit length");
        });

        yield return ("gpu_and_cpu_give_the_same_vectors", "The graphics card and the processor compute practically the same vector for the same text.", () =>
        {
            RequireModel();
            using var cpu = new OnnxEmbedder(EmbeddingModel.ModelPath, EmbeddingModel.TokenizerPath, preferGpu: false);
            string text = "passage: Der Wartungsplan für das Wochenende umfasst das Einspielen der Patches.";
            float similarity = VectorMath.Dot(Gpu.Value.Embed([text])[0], cpu.Embed([text])[0]);
            Check.That(similarity > 0.999f, $"GPU and CPU differ: similarity {similarity:F4}");
        });

        yield return ("oracle_upgrade_query_is_closest_to_the_german_upgrade_runbook", "The query \"oracle upgrade\" is more similar to a German guide for raising the database from 19c to 23ai than to unrelated texts.", () =>
        {
            RequireModel();
            string[] passages =
            [
                "passage: Anleitung zum Anheben der Datenbankversion von 19c auf 23ai: Vorabprüfungen, Parameterdatei anpassen, Datapatch ausführen, Statistiken neu sammeln.",
                "passage: Rezept für Apfelkuchen mit Zimt und Streuseln, 45 Minuten bei 180 Grad backen.",
                "passage: Quarterly sales report for the northern region with revenue by product line.",
                "passage: Urlaubsplanung für den Sommer: Flüge nach Lissabon und Hotel buchen.",
            ];
            float[] query = Gpu.Value.Embed(["query: oracle upgrade"])[0];
            float[][] vectors = Gpu.Value.Embed(passages);
            float[] scores = vectors.Select(v => VectorMath.Dot(query, v)).ToArray();
            string all = string.Join(", ", scores.Select(s => s.ToString("F3")));
            Check.That(scores[0] > scores.Skip(1).Max() + 0.02f, $"runbook is not clearly closest: {all}");
        });

        yield return ("smart_search_finds_a_related_document_without_the_query_words", "With the real model, \"oracle upgrade\" finds the German 19c-to-23ai guide by meaning and leaves the cake recipe out.", () =>
        {
            RequireModel();
            string folder = Fixtures.TempFolder("semantic");
            Fixtures.Write(folder, "anleitung_db.txt", "Anleitung zum Anheben der Datenbankversion von 19c auf 23ai. Zuerst die Vorabprüfungen ausführen, dann die Parameterdatei anpassen, danach Datapatch ausführen und die Statistiken neu sammeln.");
            Fixtures.Write(folder, "apfelkuchen.txt", "Rezept für Apfelkuchen mit Zimt und Streuseln. Den Teig kneten, die Äpfel schneiden und 45 Minuten bei 180 Grad backen.");
            Fixtures.Write(folder, "sales.txt", "Quarterly sales report for the northern region with revenue by product line and the outlook for next year.");
            Fixtures.Write(folder, "oracle upgrade termine.txt", "Termine: Montag Abstimmung, Mittwoch Test.");
            using IndexStore store = IndexStore.Open(Path.Combine(Fixtures.TempFolder("index"), "index.db"), EmbeddingModel.Id);
            using var host = new EmbedderHost(() => new Borrowed(Gpu.Value));
            using (var indexer = new Indexer(store, host) { PauseInFullScreen = false, PauseOnBattery = false })
            {
                indexer.Start([folder]);
                var watch = Stopwatch.StartNew();
                while (store.Counts().Documents < 4 && watch.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(200);
            }
            List<SmartHit> hits = new HybridSearch(store, host).Search("oracle upgrade", null, 10);
            // Every file's best similarity, so a failure shows how far each one was from the threshold.
            float[] query = Gpu.Value.Embed(["query: oracle upgrade"])[0];
            VectorTable table = store.LoadVectors();
            var names = store.AllPaths().ToDictionary(p => p.Id, p => Path.GetFileName(p.Path));
            string similarities = string.Join(", ", Enumerable.Range(0, table.Count)
                .GroupBy(i => table.FileIds[i])
                .Select(g => $"{names[g.Key]}={g.Max(i => VectorMath.Similarity(query, table.Vector(i))):F3}"));
            string list = string.Join("; ", hits.Select(h => $"{Path.GetFileName(h.Path)} {h.Kinds} {h.Similarity:F3}")) + " | " + similarities;
            Check.That(hits.Count > 0 && hits[0].Path.EndsWith("oracle upgrade termine.txt"), $"name match is not first: {list}");
            Check.That(hits.Any(h => h.Path.EndsWith("anleitung_db.txt") && h.Kinds.HasFlag(MatchKind.Meaning)), $"guide not found by meaning: {list}");
            Check.That(!hits.Any(h => h.Path.EndsWith("apfelkuchen.txt")), $"recipe should not be found: {list}");
        });

        yield return ("gpu_embeds_a_hundred_pieces_within_seconds", "One hundred pieces of about a thousand characters are embedded on the graphics card in under five seconds after warm-up.", () =>
        {
            RequireModel();
            string piece = "passage: " + string.Concat(Enumerable.Repeat("Die Datenbank wird am Wochenende auf die neue Version gebracht. ", 16));
            Gpu.Value.Embed([piece]);
            var watch = Stopwatch.StartNew();
            Gpu.Value.Embed(Enumerable.Repeat(piece, 100).ToList());
            Check.That(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed.TotalSeconds:F1} s");
        });
    }

    private static void RequireModel()
    {
        if (!EmbeddingModel.IsInstalled()) throw new SkipException("the model is not downloaded");
    }

    /// <summary>Lends the shared GPU embedder to a host without letting the host dispose it.</summary>
    private sealed class Borrowed(IEmbedder inner) : IEmbedder
    {
        public int Dimensions => inner.Dimensions;

        public string Device => inner.Device;

        public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default) => inner.Embed(texts, token);

        public void Dispose()
        {
        }
    }
}
