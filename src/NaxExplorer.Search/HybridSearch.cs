using NaxExplorer.Core;

namespace NaxExplorer.Search;

/// <summary>Why a file was found. A file can match in several ways at once.</summary>
[Flags]
public enum MatchKind
{
    None = 0,

    /// <summary>The file name contains every word.</summary>
    Name = 1,

    /// <summary>The text contains the words.</summary>
    Words = 2,

    /// <summary>The text is about the same thing, even without the same words.</summary>
    Meaning = 4,
}

public sealed record SmartHit(string Path, double Score, MatchKind Kinds, float Similarity, string? Snippet);

/// <summary>
/// The smart search: it combines three rankings of the indexed files. File names that contain every word come first,
/// then documents whose text contains the words (ranked by BM25), merged with documents whose meaning is closest to
/// the query (ranked by vector similarity). The rankings are joined with reciprocal rank fusion, which rewards files
/// that rank well in more than one list.
/// </summary>
public sealed class HybridSearch
{
    /// <summary>Weight of a name match: large enough that a file named after the query always comes first.</summary>
    internal const double NameBonus = 1.0;

    /// <summary>Rank offset of the fusion. A small value lets the top ranks of each list count for more.</summary>
    internal const double FusionK = 10;

    /// <summary>
    /// Documents found only by meaning must be at least this similar to the query. The e5 models give even unrelated
    /// texts similarities around 0.70 to 0.75, so this floor sits just above that band. Measured with this model: a
    /// German 19c-to-23ai upgrade guide scored 0.780 for "oracle upgrade", a sales report 0.741, a cake recipe 0.712.
    /// </summary>
    internal const float MinSimilarity = 0.765f;

    /// <summary>And they may be at most this much less similar than the best match.</summary>
    internal const float SimilarityWindow = 0.10f;

    /// <summary>
    /// In a larger index the floor also rises above the background: the mean similarity of all pieces to the query plus
    /// this many standard deviations, so the cut-off follows how this query relates to this collection.
    /// </summary>
    internal const float BackgroundSpread = 2.5f;

    internal const int BackgroundMinimum = 200;

    /// <summary>The lowest similarity a document found only by meaning may have.</summary>
    internal static float MeaningFloor(ReadOnlySpan<float> scores, float best)
    {
        float floor = Math.Max(MinSimilarity, best - SimilarityWindow);
        if (scores.Length < BackgroundMinimum) return floor;
        double sum = 0, squares = 0;
        foreach (float score in scores)
        {
            sum += score;
            squares += score * score;
        }
        double mean = sum / scores.Length;
        double deviation = Math.Sqrt(Math.Max(0, squares / scores.Length - mean * mean));
        return Math.Max(floor, (float)(mean + BackgroundSpread * deviation));
    }

    private const int ListDepth = 400;

    private readonly IndexStore _store;
    private readonly EmbedderHost? _host;
    private readonly Lock _cacheGate = new();
    private long _cachedVersion = -1;
    private VectorTable? _vectors;
    private Dictionary<long, string>? _paths;

    /// <summary>Without a model host the search uses names and exact words only.</summary>
    public HybridSearch(IndexStore store, EmbedderHost? host)
    {
        _store = store;
        _host = host;
    }

    /// <summary>Drops the cached vectors, for example to free memory when search has not been used for a while.</summary>
    public void ReleaseCache()
    {
        lock (_cacheGate)
        {
            _vectors = null;
            _paths = null;
            _cachedVersion = -1;
        }
    }

    private (VectorTable Vectors, Dictionary<long, string> Paths) Snapshot()
    {
        lock (_cacheGate)
        {
            long version = _store.Version;
            if (_vectors is null || _paths is null || version != _cachedVersion)
            {
                _vectors = _store.LoadVectors();
                _paths = _store.AllPaths().ToDictionary(p => p.Id, p => p.Path);
                _cachedVersion = version;
            }
            return (_vectors, _paths);
        }
    }

    /// <summary>
    /// Searches the index. Only files inside <paramref name="scope"/> are returned when it is given. The query runs on
    /// the calling thread, so call it from a background task.
    /// </summary>
    public List<SmartHit> Search(string query, string? scope, int limit, CancellationToken token = default, bool meaning = true)
    {
        List<string> terms = QueryTerms.Split(query);
        if (terms.Count == 0) return [];
        var (vectors, paths) = Snapshot();
        bool InScope(long fileId) => paths.TryGetValue(fileId, out string? path) && (scope is null || PathTools.IsSameOrInside(path, scope));

        var found = new Dictionary<long, Candidate>();
        Candidate Get(long fileId)
        {
            if (!found.TryGetValue(fileId, out Candidate? candidate)) found[fileId] = candidate = new Candidate(fileId);
            return candidate;
        }

        // 1. Names.
        string phrase = NormalizeName(query);
        var named = new List<(long Id, int Order)>();
        foreach (var (id, path) in paths)
        {
            string name = System.IO.Path.GetFileName(path);
            if (!terms.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (!InScope(id)) continue;
            // The whole query as written comes before the words in another order, and short names before long ones.
            int order = (NormalizeName(System.IO.Path.GetFileNameWithoutExtension(name)).Contains(phrase, StringComparison.OrdinalIgnoreCase) ? 0 : 10_000) + name.Length;
            named.Add((id, order));
        }
        named.Sort((a, b) => a.Order.CompareTo(b.Order));
        for (int rank = 0; rank < named.Count; rank++)
        {
            Candidate candidate = Get(named[rank].Id);
            candidate.Kinds |= MatchKind.Name;
            candidate.Score += NameBonus + 1.0 / (FusionK + rank + 1) / 10;
        }
        token.ThrowIfCancellationRequested();

        // 2. Exact words in the text: all words first; any word only when that finds too little.
        List<FullTextHit> words = _store.FullText(terms, anyWord: false, ListDepth, scope);
        bool allWords = true;
        if (words.Count < 10 && terms.Count > 1)
        {
            words = _store.FullText(terms, anyWord: true, ListDepth, scope);
            allWords = false;
        }
        int wordRank = 0;
        foreach (FullTextHit hit in words)
        {
            if (!InScope(hit.FileId)) continue;
            Candidate candidate = Get(hit.FileId);
            if ((candidate.Kinds & MatchKind.Words) != 0) continue;
            candidate.Kinds |= MatchKind.Words;
            candidate.Score += (allWords ? 1.0 : 0.6) / (FusionK + ++wordRank);
            candidate.Snippet = hit.Snippet;
        }
        token.ThrowIfCancellationRequested();

        // A single name with a filename match is a lookup, not a request for other senses of that word. Keep
        // literal content matches, but do not add semantic-only guesses (or load the model for this lookup).
        bool filenameLookup = terms.Count == 1 && named.Count > 0;
        // 3. Meaning. Descriptive queries and queries with no matching filename retain semantic retrieval.
        using EmbedderHost.Lease? lease = meaning && !filenameLookup && vectors.Count > 0 ? _host?.Acquire() : null;
        if (lease is not null)
        {
            float[] queryVector = lease.Embedder.Embed(["query: " + query], token)[0];
            // Scope before limiting and before measuring the background: unrelated folders cannot crowd out
            // the selected folder's matches or raise its relevance threshold.
            int[] scoped = Enumerable.Range(0, vectors.Count).Where(i => InScope(vectors.FileIds[i])).ToArray();
            var scores = new float[scoped.Length];
            for (int i = 0; i < scoped.Length; i++) scores[i] = VectorMath.Similarity(queryVector, vectors.Vector(scoped[i]));
            List<(int Index, float Score)> best = VectorMath.Top(scores, ListDepth * 2);
            float floor = MeaningFloor(scores, best.Count > 0 ? best[0].Score : 0);
            int meaningRank = 0;
            foreach (var (scopedIndex, score) in best)
            {
                int index = scoped[scopedIndex];
                long fileId = vectors.FileIds[index];
                if (!InScope(fileId)) continue;
                Candidate candidate = Get(fileId);
                if (candidate.Similarity < score) candidate.Similarity = score;
                if ((candidate.Kinds & MatchKind.Meaning) != 0) continue;
                // A weak semantic match only adds weight to a file found in another way; on its own it is left out.
                if (score < floor)
                {
                    if (candidate.Kinds == MatchKind.None) found.Remove(fileId);
                    continue;
                }
                candidate.Kinds |= MatchKind.Meaning;
                candidate.Score += 1.0 / (FusionK + ++meaningRank);
                candidate.BestChunk = vectors.ChunkIds[index];
            }
        }

        var ranked = found.Values.Where(c => c.Kinds != MatchKind.None).OrderByDescending(c => c.Score).ThenByDescending(c => c.Similarity).Take(limit).ToList();
        var result = new List<SmartHit>(ranked.Count);
        foreach (Candidate candidate in ranked)
        {
            string? snippet = candidate.Snippet;
            if (snippet is null && candidate.BestChunk is { } chunk) snippet = _store.ChunkText(chunk)?.Collapse().Truncate(220);
            result.Add(new SmartHit(paths[candidate.FileId], candidate.Score, candidate.Kinds, candidate.Similarity, snippet?.Collapse()));
        }
        return result;
    }

    /// <summary>File names compared as words: separators such as _ - and . count as spaces.</summary>
    internal static string NormalizeName(string name)
    {
        var chars = name.Select(c => c is '_' or '-' or '.' or '+' ? ' ' : c).ToArray();
        return new string(chars).Collapse();
    }

    private sealed class Candidate(long fileId)
    {
        public long FileId { get; } = fileId;

        public MatchKind Kinds { get; set; }

        public double Score { get; set; }

        public float Similarity { get; set; }

        public string? Snippet { get; set; }

        public long? BestChunk { get; set; }
    }
}
