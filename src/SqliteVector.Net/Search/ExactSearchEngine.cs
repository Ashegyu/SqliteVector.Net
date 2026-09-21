namespace SqliteVector.Net.Search;

using System;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;

public sealed class ExactSearchEngine
{
    private readonly SegmentReader _reader;
    private readonly VectorMetric _metric;

    public ExactSearchEngine(SegmentReader reader, VectorMetric metric)
    {
        _reader = reader;
        _metric = metric;
    }

    /// <summary>
    /// 단일 세그먼트를 순회하며 Top-K 후보를 찾습니다. 
    /// (Allocation-Free 달성을 위해 50항 금지 목록 준수: LINQ, delegate, boxing 없음)
    /// </summary>
    public void Search(ReadOnlySpan<float> query, int topK, DenseTopKBuffer buffer)
    {
        int dimensions = _reader.Header.Dimensions;
        Span<float> targetBuffer = stackalloc float[dimensions];

        var directory = _reader.Directory;
        
        // 50항: Search Hot Path 루프
        for (int i = 0; i < directory.Length; i++)
        {
            if (directory[i].Flags != RecordFlags.Committed) continue;

            // 벡터 데이터 읽기
            _reader.ReadVector(i, targetBuffer);

            // 거리 계산 (G5 SIMD)
            float score = _metric switch
            {
                VectorMetric.DotProduct => VectorMath.DotProduct(query, targetBuffer),
                VectorMetric.Cosine => VectorMath.CosineSimilarity(query, targetBuffer),
                VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, targetBuffer), // Max-Heap이므로 음수 처리
                _ => throw new NotSupportedException()
            };

            // Top-K 버퍼 삽입
            buffer.Add(1, i, score);
        }
    }
}
