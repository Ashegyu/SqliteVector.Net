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
    private readonly unsafe RecordDirectoryEntry* _directoryPtr;
    private readonly VectorMetric _metric;

    public unsafe StreamingExactSearch(string filePath, SegmentHeader header, RecordDirectoryEntry* directoryPtr, VectorMetric metric)
    {
        _filePath = filePath;
        _header = header;
        _directoryPtr = directoryPtr;
        _metric = metric;
    }

    public void Search(ReadOnlySpan<float> query, DenseTopKBuffer buffer, System.Collections.BitArray liveSet)
    {
        // OS 힌트: SequentialScan 플래그로 미리읽기(Prefetch) 유도
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        fs.Seek(_header.VectorRegionOffset, SeekOrigin.Begin);

        int capacity = _header.Capacity;
        int stride = _header.VectorStride;
        
        // 4MB 단위로 크게 읽어들이기 위한 버퍼 임대
        int blockSize = 4 * 1024 * 1024;
        int vectorsPerBlock = blockSize / stride;
        int maxBytesToRead = vectorsPerBlock * stride;
        
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(maxBytesToRead); 
        try
        {
            for (int offset = 0; offset < capacity; offset += vectorsPerBlock)
            {
                int count = Math.Min(vectorsPerBlock, capacity - offset);
                int bytesToRead = count * stride;
                
                // Block Read
                int bytesRead = 0;
                while (bytesRead < bytesToRead)
                {
                    int r = fs.Read(readBuffer, bytesRead, bytesToRead - bytesRead);
                    if (r == 0) break; // EOF
                    bytesRead += r;
                }
                
                int actualVectors = bytesRead / stride;
                
                for (int i = 0; i < actualVectors; i++)
                {
                    int globalIndex = offset + i;
                    
                    // LiveSet 필터링 (가장 최신의 물리 레코드만 검색)
                    if (!liveSet.Get(globalIndex)) continue;

                    var targetSpan = MemoryMarshal.Cast<byte, float>(new ReadOnlySpan<byte>(readBuffer, i * stride, _header.PayloadBytes));

                    float score = _metric switch
                    {
                        VectorMetric.DotProduct => VectorMath.DotProduct(query, targetSpan),
                        VectorMetric.Cosine => VectorMath.CosineSimilarity(query, targetSpan), // 필요시 정규화 처리 추가
                        VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, targetSpan),
                        _ => throw new NotSupportedException()
                    };

                    buffer.Add(1, globalIndex, score); // Segment=1 하드코딩
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
