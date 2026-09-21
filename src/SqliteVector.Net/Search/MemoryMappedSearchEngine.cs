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
    public void Search(ReadOnlySpan<float> query, DenseTopKBuffer buffer, HashSet<int>? deletedIndices = null)
    {
        int dimensions = _header.Dimensions;
        long regionOffset = _header.VectorRegionOffset;
        long stride = _header.VectorStride;
        int capacity = _header.Capacity;
        
        for (int i = 0; i < capacity; i++)
        {
            if (_directoryPtr[i].Flags != RecordFlags.Committed) continue;
            
            // G12: 논리적 삭제(Tombstone)된 레코드 건너뛰기
            if (deletedIndices != null && deletedIndices.Contains(i)) continue;

            // 벡터의 물리적 주소 계산 (Mmap 포인터 산술 연산)
            byte* vectorPtr = _basePointer + regionOffset + (i * stride);
            
            // Pointer -> Span 변환 (Zero-Copy)
            ReadOnlySpan<float> target = new ReadOnlySpan<float>(vectorPtr, dimensions);

            // SIMD 수학 연산 (VectorMath)
            float score = _metric switch
            {
                VectorMetric.DotProduct => VectorMath.DotProduct(query, target),
                VectorMetric.Cosine => VectorMath.CosineSimilarity(query, target),
                VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, target),
                _ => throw new NotSupportedException()
            };

            // ID는 추후 SQLite와 조인하기 위한 레코드 인덱스
            buffer.Add(i.ToString(), score, null);
        }
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
