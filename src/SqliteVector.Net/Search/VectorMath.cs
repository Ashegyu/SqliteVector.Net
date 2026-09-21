namespace SqliteVector.Net.Search;

using System;
using System.Numerics.Tensors;

/// <summary>
/// G5: Exact SIMD Math Engine
/// .NET 9+ TensorPrimitives를 활용하여 하드웨어 가속(AVX2/AVX-512)을 수행합니다.
/// </summary>
public static class VectorMath
{
    public static float DotProduct(ReadOnlySpan<float> query, ReadOnlySpan<float> target)
    {
        return TensorPrimitives.Dot(query, target);
    }

    public static float CosineSimilarity(ReadOnlySpan<float> query, ReadOnlySpan<float> target)
    {
        return TensorPrimitives.CosineSimilarity(query, target);
    }

    public static float EuclideanDistanceSquared(ReadOnlySpan<float> query, ReadOnlySpan<float> target)
    {
        float dist = TensorPrimitives.Distance(query, target);
        return dist * dist;
    }
}
