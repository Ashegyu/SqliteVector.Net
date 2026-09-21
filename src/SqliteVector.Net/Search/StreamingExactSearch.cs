namespace SqliteVector.Net.Search;

using System;
using System.IO;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Catalog;

/// <summary>
/// G10: Streaming Search
/// 41, 42항: MMap을 쓸 수 없을 만큼 거대한 데이터셋(Out-of-core)을 다룰 때 사용하는 Fallback 엔진.
/// FileStream과 ArrayPool을 활용하여 GC Allocation 없이 디스크를 순차(Sequential)로 긁어옵니다.
/// </summary>
public sealed class StreamingExactSearch
{
    private readonly string _filePath;
    private readonly SegmentHeader _header;
    private readonly RecordDirectoryEntry[] _directory;
    private readonly VectorMetric _metric;

    public StreamingExactSearch(string filePath, SegmentHeader header, RecordDirectoryEntry[] directory, VectorMetric metric)
    {
        _filePath = filePath;
        _header = header;
        _directory = directory;
        _metric = metric;
    }

    public void Search(ReadOnlySpan<float> query, DenseTopKBuffer buffer, HashSet<int>? deletedIndices = null)
    {
        // OS 힌트: SequentialScan 전달로 미리읽기(Prefetch) 가속
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        fs.Seek(_header.VectorRegionOffset, SeekOrigin.Begin);

        int capacity = _header.Capacity;
        int stride = _header.VectorStride;
        
        // 블록 단위로 읽어오기 위한 ArrayPool 대여 (Zero-Allocation)
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(stride); 
        try
        {
            for (int i = 0; i < capacity; i++)
            {
                var entry = _directory[i];
                fs.ReadExactly(readBuffer, 0, stride); 
                
                if (entry.Flags != RecordFlags.Committed) continue;
                if (deletedIndices != null && deletedIndices.Contains(i)) continue;

                var targetSpan = MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(readBuffer, 0, _header.PayloadBytes));

                float score = _metric switch
                {
                    VectorMetric.DotProduct => VectorMath.DotProduct(query, targetSpan),
                    VectorMetric.Cosine => VectorMath.CosineSimilarity(query, targetSpan),
                    VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, targetSpan),
                    _ => throw new NotSupportedException()
                };

                buffer.Add(i.ToString(), score, null);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
