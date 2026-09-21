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
    public long SegmentId { get; }
    public string FilePath { get; }
    public SegmentHeader Header { get; }
    public unsafe RecordDirectoryEntry* DirectoryPtr { get; }

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePointer;
    
    private readonly VectorMetric _metric;

    private readonly bool _isNormalized;

    private readonly long _fileLength; // Added to check bounds during access

    public MemoryMappedSearchEngine(long segmentId, string filePath, VectorMetric metric, bool isNormalized = false, bool isActive = false)
    {
        SegmentId = segmentId;
        FilePath = filePath;
        _metric = metric;
        _isNormalized = isNormalized;
        
        // 1. 읽기 전용으로 열기 (Writer와 충돌하지 않도록 FileShare.ReadWrite 필수)
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        
        try
        {
            long fileLength = fs.Length;
            _fileLength = fileLength;
            
            // 2. 헤더 먼저 FileStream으로 읽기 (안전한 Managed 방식)
            if (fileLength < Marshal.SizeOf<SegmentHeader>())
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.TruncatedSegment, segmentId, filePath, "File is smaller than header size.");
                
            Span<byte> headerBytes = stackalloc byte[Marshal.SizeOf<SegmentHeader>()];
            fs.ReadExactly(headerBytes);
            Header = MemoryMarshal.Read<SegmentHeader>(headerBytes);
            
            // 3. 강력한 검증 (Unsafe 메모리 맵핑 전 필수)
            uint expectedChecksum;
            unsafe
            {
                fixed (byte* p = headerBytes)
                {
                    expectedChecksum = System.IO.Hashing.Crc32.HashToUInt32(new ReadOnlySpan<byte>(p, 124));
                }
            }
            if (Header.HeaderChecksum != expectedChecksum && Header.HeaderChecksum != 0) // Allow 0 for backward compatibility during this transition
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.HeaderChecksum, segmentId, filePath, $"Header Checksum Mismatch. Expected {expectedChecksum}, got {Header.HeaderChecksum}");

            if (Header.Magic != SegmentHeader.MagicNumber)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidMagic, segmentId, filePath, "Invalid Segment Magic Number");
                
            if (Header.FormatVersion != 2)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, $"Unsupported FormatVersion: {Header.FormatVersion}");
            
            if (Header.SegmentId != segmentId)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, $"Segment Identity Mismatch: Expected {segmentId}, got {Header.SegmentId}");
                
            if (Header.Dimensions <= 0 || Header.PayloadBytes != Header.Dimensions * 4)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, $"Invalid Dimensions or PayloadBytes");
                
            if (Header.Alignment != 64 || Header.VectorStride < Header.PayloadBytes || Header.VectorStride % Header.Alignment != 0)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "Invalid Alignment or VectorStride");
                
            if (Header.DirectoryOffset < Marshal.SizeOf<SegmentHeader>())
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "Invalid DirectoryOffset");
                
            long directoryEnd;
            try
            {
                directoryEnd = checked(Header.DirectoryOffset + ((long)Header.Capacity * Marshal.SizeOf<RecordDirectoryEntry>()));
                if (directoryEnd > Header.VectorRegionOffset)
                    throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "Directory overlaps VectorRegionOffset");
            }
            catch (OverflowException)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "Overflow in Directory offset calculation");
            }
                
            if (Header.VectorRegionOffset % Header.Alignment != 0)
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "VectorRegionOffset is not aligned");
                
            long expectedMinSize;
            try
            {
                expectedMinSize = checked(Header.VectorRegionOffset + ((long)Header.Capacity * Header.VectorStride));
            }
            catch (OverflowException)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.InvalidLayout, segmentId, filePath, "Overflow in Vector region offset calculation");
            }
            
            // Truncated Sealed Segment 검사
            if (!isActive && fileLength < expectedMinSize)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.TruncatedSegment, segmentId, filePath, $"Sealed file size {fileLength} is smaller than expected {expectedMinSize}");
            }

            // 4. 이제 안전하게 MMap 생성 (fs 재사용)
            // fileLength만큼만 매핑 (0을 주면 파일 전체)
            _mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePointer = ptr;

            // 5. 안전하게 검증된 오프셋을 사용해 포인터 캐스팅 (Zero-Copy)
            DirectoryPtr = (RecordDirectoryEntry*)(_basePointer + Header.DirectoryOffset);
            
            // fs는 _mmf가 소유하게 되므로 여기서 Dispose하지 않음
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public unsafe void ValidateLiveRecords(System.Collections.BitArray liveSet)
    {
        for (int i = 0; i < liveSet.Count; i++)
        {
            if (!liveSet[i]) continue;

            var entry = DirectoryPtr[i];
            if (entry.Flags != RecordFlags.Committed)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(
                    SqliteVector.Net.Exceptions.CorruptionKind.UnknownRecordFlags, 
                    SegmentId, 
                    FilePath, 
                    $"Catalog references record {i} but physical flag is {entry.Flags}",
                    i);
            }

            long offset = Header.VectorRegionOffset + ((long)i * Header.VectorStride);
            
            // 카탈로그가 참조하는 레코드(LiveSet=true)인데 파일이 잘려있다면 심각한 Corruption(Data Loss)이다.
            if (offset + Header.PayloadBytes > _fileLength)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(
                    SqliteVector.Net.Exceptions.CorruptionKind.TruncatedSegment, 
                    SegmentId, 
                    FilePath, 
                    $"Catalog references record {i} but it is truncated. Offset: {offset}, FileLength: {_fileLength}",
                    i);
            }

            // Check CRC
            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(_basePointer + offset, Header.PayloadBytes);
            uint crc = System.IO.Hashing.Crc32.HashToUInt32(payload);
            if (crc != entry.PayloadCRC)
            {
                throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(
                    SqliteVector.Net.Exceptions.CorruptionKind.PayloadChecksum, 
                    SegmentId, 
                    FilePath, 
                    $"Payload CRC mismatch for record {i}. Expected {entry.PayloadCRC}, got {crc}",
                    i);
            }
        }
    }

    /// <summary>
    /// G5 SIMD + G9 MMap 단일 스레드 검색
    /// </summary>
    public void Search(ReadOnlySpan<float> query, DenseTopKBuffer buffer, System.Collections.BitArray? liveSet = null)
    {
        int dimensions = Header.Dimensions;
        long regionOffset = Header.VectorRegionOffset;
        long stride = Header.VectorStride;
        int capacity = Header.Capacity;
        
        Console.WriteLine($"[Search] Engine for segment {SegmentId} searching {capacity} records (LiveSet={liveSet?.Count ?? 0}).");

        for (int i = 0; i < capacity; i++)
        {
            // B6: Both logical (LiveSet) and physical visibility must be checked
            if (liveSet != null && !liveSet.Get(i)) continue;
            if (DirectoryPtr[i].Flags != RecordFlags.Committed) continue;

            // 벡터 물리 주소 계산
            byte* vectorPtr = _basePointer + regionOffset + (i * stride);
            ReadOnlySpan<float> target = new ReadOnlySpan<float>(vectorPtr, dimensions);

            float score = _metric switch
            {
                VectorMetric.DotProduct => VectorMath.DotProduct(query, target),
                VectorMetric.Cosine => _isNormalized ? VectorMath.DotProduct(query, target) : VectorMath.CosineSimilarity(query, target),
                VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(query, target),
                _ => throw new NotSupportedException()
            };

            // O(N) 자원 할당 없이 구조체 배열에 삽입
            buffer.Add(SegmentId, i, score);
        }
    }

    /// <summary>
    /// 멀티 코어를 100% 활용하는 병렬 SIMD 검색
    /// </summary>
    public void SearchParallel(ReadOnlySpan<float> query, DenseTopKBuffer globalBuffer, System.Collections.BitArray? liveSet = null)
    {
        int capacity = Header.Capacity;
        int dimensions = Header.Dimensions;
        long regionOffset = Header.VectorRegionOffset;
        long stride = Header.VectorStride;
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
                    // B6: Both logical and physical visibility
                    if (liveSet != null && !liveSet.Get(i)) continue;
                    if (DirectoryPtr[i].Flags != RecordFlags.Committed) continue;

                    byte* vectorPtr = _basePointer + regionOffset + (i * stride);
                    ReadOnlySpan<float> target = new ReadOnlySpan<float>(vectorPtr, dimensions);

                    float score = _metric switch
                    {
                        VectorMetric.DotProduct => VectorMath.DotProduct(localQuery, target),
                        VectorMetric.Cosine => _isNormalized ? VectorMath.DotProduct(localQuery, target) : VectorMath.CosineSimilarity(localQuery, target),
                        VectorMetric.EuclideanSquared => -VectorMath.EuclideanDistanceSquared(localQuery, target),
                        _ => throw new NotSupportedException()
                    };

                    localBuffer.Add(SegmentId, i, score);
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

    private int _refCount = 1;

    public bool TryAddReference()
    {
        while (true)
        {
            int current = _refCount;
            if (current == 0) return false;
            
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            if (_basePointer != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }

    public void Dispose()
    {
        Release();
    }
}

