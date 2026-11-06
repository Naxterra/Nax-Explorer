namespace NaxExplorer.Search;

/// <summary>
/// Vector helpers. Stored vectors are unit length and kept as signed bytes (value times 127), which needs a quarter of
/// the space of floats and changes similarities by well under one percent.
/// </summary>
public static class VectorMath
{
    public const float Scale = 127f;

    public static void Normalize(Span<float> vector)
    {
        double sum = 0;
        foreach (float value in vector) sum += value * value;
        if (sum <= 0) return;
        float factor = (float)(1 / Math.Sqrt(sum));
        for (int i = 0; i < vector.Length; i++) vector[i] *= factor;
    }

    public static sbyte[] Quantize(ReadOnlySpan<float> unitVector)
    {
        var result = new sbyte[unitVector.Length];
        for (int i = 0; i < unitVector.Length; i++)
        {
            float scaled = MathF.Round(unitVector[i] * Scale);
            result[i] = (sbyte)Math.Clamp(scaled, -127f, 127f);
        }
        return result;
    }

    /// <summary>The cosine similarity of a float query and a stored byte vector.</summary>
    public static float Similarity(ReadOnlySpan<float> query, ReadOnlySpan<sbyte> stored)
    {
        float sum = 0;
        for (int i = 0; i < query.Length; i++) sum += query[i] * stored[i];
        return sum / Scale;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>The indexes of the <paramref name="count"/> highest scores, best first.</summary>
    public static List<(int Index, float Score)> Top(ReadOnlySpan<float> scores, int count)
    {
        var heap = new PriorityQueue<int, float>(count + 1);
        for (int i = 0; i < scores.Length; i++)
        {
            if (heap.Count < count)
            {
                heap.Enqueue(i, scores[i]);
            }
            else if (heap.TryPeek(out _, out float lowest) && scores[i] > lowest)
            {
                heap.DequeueEnqueue(i, scores[i]);
            }
        }
        var result = new List<(int, float)>(heap.Count);
        while (heap.TryDequeue(out int index, out float score)) result.Add((index, score));
        result.Reverse();
        return result;
    }
}
