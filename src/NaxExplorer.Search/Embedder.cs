using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace NaxExplorer.Search;

/// <summary>Turns texts into normalized vectors of meaning. Vectors of related texts point in similar directions.</summary>
public interface IEmbedder : IDisposable
{
    int Dimensions { get; }

    /// <summary>A short description of where the model runs, such as "GPU (DirectML)" or "CPU".</summary>
    string Device { get; }

    /// <summary>One unit-length vector per text, in the same order.</summary>
    float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default);
}

/// <summary>
/// The XLM-RoBERTa tokenizer that the e5 models use. The SentencePiece model numbers its pieces differently from the
/// model's vocabulary: every piece moves up by one, and the first four ids are the special tokens.
/// </summary>
public sealed class XlmrTokenizer
{
    public const int Bos = 0;
    public const int Pad = 1;
    public const int Eos = 2;
    public const int Unknown = 3;

    private readonly SentencePieceTokenizer _tokenizer;

    public XlmrTokenizer(string modelPath)
    {
        using FileStream stream = File.OpenRead(modelPath);
        // Without the sentence markers: Encode adds the model's own ids for them.
        _tokenizer = SentencePieceTokenizer.Create(stream, false, false);
    }

    /// <summary>The model's token ids for a text, starting with &lt;s&gt; and ending with &lt;/s&gt;, at most <paramref name="maxTokens"/> long.</summary>
    public long[] Encode(string text, int maxTokens)
    {
        IReadOnlyList<int> pieces = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        int count = Math.Min(pieces.Count, maxTokens - 2);
        long[] ids = new long[count + 2];
        ids[0] = Bos;
        for (int i = 0; i < count; i++)
        {
            int piece = pieces[i];
            ids[i + 1] = piece == 0 ? Unknown : piece + 1;
        }
        ids[^1] = Eos;
        return ids;
    }
}

/// <summary>
/// Runs the e5 model with ONNX Runtime. It uses the graphics card through DirectML when it can and falls back to the
/// processor. Texts are processed in batches of similar length, and the token states are averaged into one vector.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder
{
    private const int BatchSize = 32;

    private readonly InferenceSession _session;
    private readonly XlmrTokenizer _tokenizer;
    private readonly bool _needsTokenTypes;
    private readonly string _outputName;
    private readonly Lock _gate = new();

    public int Dimensions => EmbeddingModel.Dimensions;

    public string Device { get; }

    public OnnxEmbedder(string modelPath, string tokenizerPath, bool preferGpu)
    {
        _tokenizer = new XlmrTokenizer(tokenizerPath);
        InferenceSession? session = null;
        string device = "CPU";
        if (preferGpu)
        {
            try
            {
                var gpu = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // DirectML needs these two settings.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                gpu.AppendExecutionProvider_DML(0);
                session = new InferenceSession(modelPath, gpu);
                device = "GPU (DirectML)";
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                session = null;
            }
        }
        if (session is null)
        {
            var cpu = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // Leaves half the cores to the user, since indexing runs in the background.
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            };
            session = new InferenceSession(modelPath, cpu);
        }
        _session = session;
        Device = device;
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public float[][] Embed(IReadOnlyList<string> texts, CancellationToken token = default)
    {
        var encoded = new long[texts.Count][];
        for (int i = 0; i < texts.Count; i++) encoded[i] = _tokenizer.Encode(texts[i], EmbeddingModel.MaxTokens);
        // Similar lengths share a batch, so little time is spent on padding.
        int[] order = Enumerable.Range(0, texts.Count).OrderBy(i => encoded[i].Length).ToArray();
        var result = new float[texts.Count][];
        for (int start = 0; start < order.Length; start += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            int[] batch = order[start..Math.Min(order.Length, start + BatchSize)];
            float[][] vectors = RunBatch(batch.Select(i => encoded[i]).ToArray());
            for (int j = 0; j < batch.Length; j++) result[batch[j]] = vectors[j];
        }
        return result;
    }

    private float[][] RunBatch(long[][] sequences)
    {
        int batch = sequences.Length;
        int longest = sequences.Max(s => s.Length);
        // Lengths are rounded up to a multiple of 32, which keeps the number of different shapes DirectML sees small.
        int length = Math.Min(EmbeddingModel.MaxTokens, (longest + 31) / 32 * 32);
        long[] ids = new long[batch * length];
        long[] mask = new long[batch * length];
        Array.Fill(ids, XlmrTokenizer.Pad);
        for (int b = 0; b < batch; b++)
        {
            long[] sequence = sequences[b];
            Array.Copy(sequence, 0, ids, b * length, sequence.Length);
            Array.Fill(mask, 1L, b * length, sequence.Length);
        }
        long[] shape = [batch, length];
        using OrtValue idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using OrtValue maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using OrtValue? typesValue = _needsTokenTypes ? OrtValue.CreateTensorValueFromMemory(new long[batch * length], shape) : null;
        var names = new List<string> { "input_ids", "attention_mask" };
        var values = new List<OrtValue> { idsValue, maskValue };
        if (typesValue is not null)
        {
            names.Add("token_type_ids");
            values.Add(typesValue);
        }
        using var options = new RunOptions();
        lock (_gate)
        {
            using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(options, names, values, [_outputName]);
            ReadOnlySpan<float> hidden = outputs[0].GetTensorDataAsSpan<float>();
            int dimensions = Dimensions;
            var vectors = new float[batch][];
            for (int b = 0; b < batch; b++)
            {
                // Mean of the token states of the real tokens, then scaled to unit length.
                var vector = new float[dimensions];
                int tokens = sequences[b].Length;
                for (int t = 0; t < tokens; t++)
                {
                    ReadOnlySpan<float> state = hidden.Slice((b * length + t) * dimensions, dimensions);
                    for (int d = 0; d < dimensions; d++) vector[d] += state[d];
                }
                VectorMath.Normalize(vector);
                vectors[b] = vector;
            }
            return vectors;
        }
    }

    public void Dispose() => _session.Dispose();
}
