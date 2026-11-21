using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        if (stored.Length < query.Length) throw new ArgumentException("Vector lengths must match.", nameof(stored));
        int length = query.Length;
        int i = 0;
        float sum = 0;
        if (Avx2.IsSupported && length >= 16)
        {
            Vector256<float> acc0 = Vector256<float>.Zero;
            Vector256<float> acc1 = Vector256<float>.Zero;
            ref float qRef = ref MemoryMarshal.GetReference(query);
            ref sbyte sRef = ref MemoryMarshal.GetReference(stored);
            for (; i <= length - 16; i += 16)
            {
                Vector128<sbyte> bytes = Vector128.LoadUnsafe(ref sRef, (nuint)i);
                Vector256<short> shorts = Avx2.ConvertToVector256Int16(bytes);
                Vector256<float> low = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(shorts.GetLower()));
                Vector256<float> high = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(shorts.GetUpper()));
                Vector256<float> q0 = Vector256.LoadUnsafe(ref qRef, (nuint)i);
                Vector256<float> q1 = Vector256.LoadUnsafe(ref qRef, (nuint)(i + 8));
                if (Fma.IsSupported)
                {
                    acc0 = Fma.MultiplyAdd(q0, low, acc0);
                    acc1 = Fma.MultiplyAdd(q1, high, acc1);
                }
                else
                {
                    acc0 = Avx.Add(acc0, Avx.Multiply(q0, low));
                    acc1 = Avx.Add(acc1, Avx.Multiply(q1, high));
                }
            }
            sum = Vector256.Sum(Avx.Add(acc0, acc1));
        }
        for (; i < length; i++) sum += query[i] * stored[i];
        return sum / Scale;
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (b.Length < a.Length) throw new ArgumentException("Vector lengths must match.", nameof(b));
        int i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            int step = Vector<float>.Count;
            for (; i <= a.Length - step; i += step)
                acc += new Vector<float>(a.Slice(i, step)) * new Vector<float>(b.Slice(i, step));
            sum = Vector.Sum(acc);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
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
