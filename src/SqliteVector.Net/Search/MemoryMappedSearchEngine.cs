namespace SqliteVector.Net.Search;

using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;

/// <summary>
/// G9: MemoryMapped Exact Search
/// 40항: MemoryMappedFile을 사용하여 OS 가상 메모리 공간에 파일을 매핑합니다.
/// Unsafe 포인터를 직접 Span으로 캐스팅하여 데이터 복사(Copy)와 메모리 할당(Allocation)이 0 바이트입니다.
/// </summary>
public sealed unsafe class MemoryMappedSearchEngine : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePointer;
    
    private readonly SegmentHeader _header;
    private readonly RecordDirectoryEntry* _directoryPtr;
    private readonly VectorMetric _metric;

    public MemoryMappedSearchEngine(string filePath, VectorMetric metric)
    {
        _metric = metric;
        
        // 1. 읽기 전용 매핑 (Writer와 경합하지 않도록 FileShare.ReadWrite 필수)
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePointer = ptr;

        // 2. 포인터에서 직접 Header 역직렬화 (Zero-Copy)
        _header = MemoryMarshal.Read<SegmentHeader>(new ReadOnlySpan<byte>(_basePointer, Marshal.SizeOf<SegmentHeader>()));

        if (_header.Magic != SegmentHeader.MagicNumber)
            throw new InvalidDataException("Invalid Segment Magic Number");

        // 3. 디렉터리 포인터 캐싱 (Zero-Copy)
        _directoryPtr = (RecordDirectoryEntry*)(_basePointer + _header.DirectoryOffset);
    }

    /// <summary>
    /// G5 SIMD + G9 MMap 결합 스캔 루프
    /// 초당 기가바이트(GB/s) 단위로 메모리를 읽어들이며 L1/L2 캐시를 극한으로 활용합니다.
    /// </summary>
    public void Search(ReadOnlySpan<float> query, DenseTopKBuffer buffer, System.Collections.BitArray? liveSet = null)
    {
        int dimensions = _header.Dimensions;
        long regionOffset = _header.VectorRegionOffset;
        long stride = _header.VectorStride;
        int capacity = _header.Capacity;
        
        for (int i = 0; i < capacity; i++)
        {
            // 물리적 레코드 무시: LiveSet(논리적 View)에 없으면 즉시 스킵 (과거 버전, 지워진 버전 방어)
            if (liveSet != null && !liveSet.Get(i)) continue;
            
            // 만약 LiveSet이 없는 경우(V1 하위호환) 물리적 커밋 여부만 확인
            if (liveSet == null && _directoryPtr[i].Flags != RecordFlags.Committed) continue;

            // 벡터의 물리적 주소 계산
            byte* vectorPtr = _basePointer + regionOffset + (i * stride);
            ReadOnlySpan<float> target = new ReadOnlySpan<float>(vectorPtr, dimensions);

            float score = _metric switch
            {
                VectorMetric.DotProduct => VectorMath.DotProduct(query, target),
                VectorMetric.Cosine => VectorMath.CosineSimilarity(query, target),
                VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, target),
                _ => throw new NotSupportedException()
            };

            // O(N) 문자열 할당을 없애고 값(Primitive)만 버퍼에 추가
            buffer.Add(1, i, score);
        }
    }

    /// <summary>
    /// 멀티 코어를 100% 활용하는 병렬(Map-Reduce) SIMD 스캔
    /// 스레드별 로컬 버퍼를 사용하여 Lock 경합을 완전히 제거했습니다.
    /// </summary>
    public void SearchParallel(ReadOnlySpan<float> query, DenseTopKBuffer globalBuffer, System.Collections.BitArray? liveSet = null)
    {
        int capacity = _header.Capacity;
        int dimensions = _header.Dimensions;
        long regionOffset = _header.VectorRegionOffset;
        long stride = _header.VectorStride;
        int topK = globalBuffer.Capacity;
        
        float[] queryArray = query.ToArray();
        object syncRoot = new object();

        var rangePartitioner = System.Collections.Concurrent.Partitioner.Create(0, capacity);

        System.Threading.Tasks.Parallel.ForEach(
            rangePartitioner,
            () => new DenseTopKBuffer(topK),
            (range, loopState, localBuffer) =>
            {
                ReadOnlySpan<float> localQuery = queryArray;
                
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (liveSet != null && !liveSet.Get(i)) continue;
                    if (liveSet == null && _directoryPtr[i].Flags != RecordFlags.Committed) continue;

                    byte* vectorPtr = _basePointer + regionOffset + (i * stride);
                    ReadOnlySpan<float> target = new ReadOnlySpan<float>(vectorPtr, dimensions);

                    float score = _metric switch
                    {
                        VectorMetric.DotProduct => VectorMath.DotProduct(localQuery, target),
                        VectorMetric.Cosine => VectorMath.CosineSimilarity(localQuery, target),
                        VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(localQuery, target),
                        _ => throw new NotSupportedException()
                    };

                    localBuffer.Add(1, i, score);
                }
                return localBuffer;
            },
            (localBuffer) =>
            {
                lock (syncRoot)
                {
                    var results = localBuffer.GetSortedResults();
                    foreach (var res in results)
                    {
                        globalBuffer.Add(res.SegmentId, res.RecordIndex, res.Score);
                    }
                }
            }
        );
    }

    public void Dispose()
    {
        if (_basePointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
