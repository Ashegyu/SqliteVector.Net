namespace SqliteVector.Net;

using System;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

/// <summary>
/// SIMD 하드웨어 가속을 사용하는 코어 연산 엔진
/// </summary>
public static class VectorMath
{
    // Zero-allocation 코사인 유사도/내적 연산 (벡터가 정규화되어 있다고 가정)
    public static float CalculateSimilarity(ReadOnlySpan<float> query, ReadOnlySpan<byte> targetBlob)
    {
        // SQLite에서 읽어온 순수 바이트 배열을 float Span으로 포인터 캐스팅 (메모리 재할당/GC 발생 0)
        ReadOnlySpan<float> target = MemoryMarshal.Cast<byte, float>(targetBlob);
        
        // AVX2 / AVX-512 등의 SIMD 명령어를 사용하여 연산
        return TensorPrimitives.Dot(query, target);
    }
}
