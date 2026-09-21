namespace SqliteVector.Net.Tests;

using System;
using System.Collections.Generic;

/// <summary>
/// G0 Baseline: SIMD를 사용하지 않는 순수 C# 구현체.
/// 향후 MMap, Quantization 등 모든 최적화 구현의 Ground Truth 역할을 합니다.
/// </summary>
public static class ScalarOracle
{
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimensions must match");

        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Dimensions must match");

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0f;

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
